// Command marvin-site serves the Marvin project website behind a GitHub sign-in.
//
// The site in docs/ is plain static HTML. Published on GitHub Pages it would be
// readable by anyone holding the URL — Pages offers no access control below the
// Enterprise tier — and that is the gap this binary fills. It serves the same
// files unchanged, but only to visitors who have signed in with GitHub and whose
// account is on an allowlist.
//
// It is built for Cloud Run: it listens on $PORT, holds no server-side state, is
// safe to run as many concurrent instances, and shuts down cleanly on SIGTERM.
package main

import (
	"context"
	"crypto/subtle"
	"errors"
	"fmt"
	"html"
	"io/fs"
	"log"
	"net"
	"net/http"
	"net/url"
	"os"
	"os/signal"
	"strconv"
	"strings"
	"sync/atomic"
	"syscall"
	"time"

	"github.com/spedemon/marvin/server/device"
	"github.com/spedemon/marvin/server/harness"
	"github.com/spedemon/marvin/server/provider"
)

const userAgent = "marvin-site/1.0 (+https://github.com/spedemon/marvin)"

// minSessionKeyLen is the shortest signing key we will start with. Below this a
// key is brute-forceable, and forging a session cookie means bypassing sign-in
// altogether.
const minSessionKeyLen = 32

type config struct {
	port          string
	docsDir       string
	controllerDir string
	clientID      string
	clientSecret  string
	sessionKey    []byte
	baseURL       string // optional; empty means derive from the request
	sessionTTL    time.Duration

	allowedUsers map[string]bool // lower-cased GitHub logins
	allowedOrgs  []string        // lower-cased GitHub org logins
	allowAnyUser bool

	// localMode drops the sign-in so the system can be run on a bench with the
	// robot on the same Wi-Fi. See local.go for what holds the line instead.
	localMode bool

	// Voice. Keys come from the environment — Secret Manager on Google, Secrets
	// Manager on AWS — for the same reason the session key does: the server
	// keeps no state, so there is nowhere else every instance would agree on.
	aiProvider     string
	aiKeys         map[string]string
	aiModel        string
	aiVoice        string
	aiInstructions string
	deviceTokenTTL time.Duration
}

type app struct {
	cfg        config
	files      http.Handler
	controller http.Handler
	hc         *http.Client

	// Voice. Nil on an app built without setupVoice, which is how the website
	// tests keep running against exactly the routes they were written for.
	hub            *device.Hub
	providers      *provider.Set
	chain          harness.Chain
	activeProvider atomic.Pointer[string]
}

func main() {
	log.SetFlags(log.LstdFlags | log.LUTC)

	cfg, err := loadConfig()
	if err != nil {
		log.Fatalf("configuration: %v", err)
	}
	if err := run(cfg); err != nil {
		log.Fatal(err)
	}
}

func run(cfg config) error {
	if _, err := os.Stat(cfg.docsDir); err != nil {
		return fmt.Errorf("site directory %q: %w", cfg.docsDir, err)
	}

	a := newApp(cfg)

	srv := &http.Server{
		Addr:              net.JoinHostPort("", cfg.port),
		Handler:           a.routes(),
		ReadHeaderTimeout: 10 * time.Second,
		ReadTimeout:       30 * time.Second,
		// Generous: the site ships a 7 MB demo video and a slow phone on a
		// train should still finish the download.
		WriteTimeout: 5 * time.Minute,
		IdleTimeout:  2 * time.Minute,
	}

	// Cloud Run sends SIGTERM and then waits before killing the container.
	// Draining here means an in-flight page load is not cut off mid-response.
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()

	if cfg.localMode {
		warnAboutLocalMode(cfg.port)
	}

	errCh := make(chan error, 1)
	go func() {
		log.Printf("serving %s on port %s (%s)", cfg.docsDir, cfg.port, cfg.accessSummary())
		if err := srv.ListenAndServe(); err != nil && !errors.Is(err, http.ErrServerClosed) {
			errCh <- err
		}
	}()

	select {
	case err := <-errCh:
		return err
	case <-ctx.Done():
		log.Print("shutting down")
		shutdownCtx, cancel := context.WithTimeout(context.Background(), 15*time.Second)
		defer cancel()
		return srv.Shutdown(shutdownCtx)
	}
}

// newApp builds the server, voice included.
func newApp(cfg config) *app {
	a := &app{
		cfg:   cfg,
		files: http.FileServer(noListingFS{http.Dir(cfg.docsDir)}),
		// The robot's own WebSocket session can run for the full hour Cloud Run
		// allows, so this client's timeout applies to GitHub calls only; the
		// providers dial with their own contexts.
		hc: &http.Client{Timeout: 15 * time.Second},
	}
	if cfg.controllerDir != "" {
		a.controller = http.FileServer(noListingFS{http.Dir(cfg.controllerDir)})
	}
	a.setupVoice()
	return a
}

func (a *app) routes() http.Handler {
	mux := http.NewServeMux()

	// Unauthenticated: the liveness probe and the sign-in flow itself.
	mux.HandleFunc("GET /healthz", func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Cache-Control", "no-store")
		fmt.Fprintln(w, "ok")
	})
	mux.HandleFunc("GET "+signinPath, a.handleSignin)
	mux.HandleFunc("GET "+loginPath, a.handleLogin)
	mux.HandleFunc("GET "+callbackPath, a.handleCallback)
	mux.HandleFunc("GET "+logoutPath, a.handleLogout)

	// The robot. Authenticated by its own signed token, not by GitHub: there is
	// no browser here and nobody to sign in.
	if a.hub != nil {
		mux.Handle("/v1/device", a.deviceHandler())

		// The controller and its API, behind the same sign-in as the website.
		mux.Handle("GET /api/status", a.requireAuth(http.HandlerFunc(a.handleStatus)))
		mux.Handle("POST /api/provider", a.requireAuth(http.HandlerFunc(a.handleSelectProvider)))
		mux.Handle("POST /api/device-token", a.requireAuth(http.HandlerFunc(a.handleDeviceToken)))
		mux.Handle("POST /api/device-action", a.requireAuth(http.HandlerFunc(a.handleDeviceAction)))
		mux.Handle("GET /ws/controller", a.requireAuth(http.HandlerFunc(a.handleControllerSocket)))
	}
	if a.controller != nil {
		mux.Handle("/app/", a.requireAuth(http.StripPrefix("/app", a.serveControllerHandler())))
		mux.HandleFunc("GET /app", func(w http.ResponseWriter, r *http.Request) {
			http.Redirect(w, r, "/app/", http.StatusMovedPermanently)
		})
	}

	// Everything else is the website, and needs a session.
	mux.Handle("/", a.requireAuth(http.HandlerFunc(a.serveSite)))

	if a.cfg.localMode {
		// Outermost, so it covers the health check and the sign-in routes too.
		return localOnly(securityHeaders(mux))
	}
	return securityHeaders(mux)
}

// serveSite hands the request to the static file server. Responses are marked
// private so that no shared cache between here and the visitor keeps a copy of
// a page the next visitor has not earned.
func (a *app) serveSite(w http.ResponseWriter, r *http.Request) {
	w.Header().Set("Cache-Control", "private, no-cache")
	a.files.ServeHTTP(w, r)
}

// requireAuth admits requests carrying a valid session and diverts the rest to
// the sign-in page.
func (a *app) requireAuth(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if a.cfg.localMode {
			next.ServeHTTP(w, r)
			return
		}
		if _, ok := a.currentSession(r); !ok {
			a.redirectToSignin(w, r, r.URL.RequestURI())
			return
		}
		next.ServeHTTP(w, r)
	})
}

func (a *app) currentSession(r *http.Request) (session, bool) {
	if a.cfg.localMode {
		// Nobody signed in, but the handlers that log who did something still
		// need a name to put in the line.
		return session{Login: "local"}, true
	}
	c, err := r.Cookie(sessionCookieName)
	if err != nil {
		return session{}, false
	}
	s, err := decodeSession(c.Value, a.cfg.sessionKey, time.Now())
	if err != nil {
		return session{}, false
	}
	return s, true
}

func (a *app) redirectToSignin(w http.ResponseWriter, r *http.Request, ret string) {
	// A stale or forged cookie would otherwise bounce the visitor round this
	// loop forever.
	clearCookie(w, r, sessionCookieName)
	target := signinPath
	if ret = safeReturnPath(ret); ret != "/" {
		target += "?return=" + url.QueryEscape(ret)
	}
	w.Header().Set("Cache-Control", "no-store")
	http.Redirect(w, r, target, http.StatusFound)
}

// baseURL is the origin GitHub must redirect back to. Configuring it explicitly
// is best, but deriving it works too: the callback URL registered at GitHub is
// checked against this value by GitHub itself, so a forged Host header produces
// a rejected sign-in rather than a redirect anywhere useful to an attacker.
func (a *app) baseURL(r *http.Request) string {
	if a.cfg.baseURL != "" {
		return a.cfg.baseURL
	}
	scheme := "http"
	if isHTTPS(r) {
		scheme = "https"
	}
	host := r.Header.Get("X-Forwarded-Host")
	if host == "" {
		host = r.Host
	}
	return scheme + "://" + host
}

// securityHeaders applies the headers that are unambiguously right for a gated
// static site. There is deliberately no Content-Security-Policy: the pages carry
// inline scripts (the pre-paint theme switch) and pull fonts from Google, so any
// honest policy would need 'unsafe-inline' and buy little. Adding one is a
// change to make against the site, not the server.
func securityHeaders(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		h := w.Header()
		h.Set("X-Content-Type-Options", "nosniff")
		h.Set("X-Frame-Options", "DENY")
		h.Set("Referrer-Policy", "strict-origin-when-cross-origin")
		next.ServeHTTP(w, r)
	})
}

// noListingFS serves files but refuses to render directory indexes. Left to
// itself http.FileServer lists any folder without an index.html, which on this
// repository would expose the asset tree.
type noListingFS struct{ fs http.FileSystem }

func (n noListingFS) Open(name string) (http.File, error) {
	f, err := n.fs.Open(name)
	if err != nil {
		return nil, err
	}
	info, err := f.Stat()
	if err != nil {
		f.Close()
		return nil, err
	}
	if info.IsDir() {
		index, err := n.fs.Open(strings.TrimSuffix(name, "/") + "/index.html")
		if err != nil {
			f.Close()
			return nil, fs.ErrNotExist
		}
		index.Close()
	}
	return f, nil
}

func loadConfig() (config, error) {
	cfg := config{
		port:          envOr("PORT", "8080"),
		docsDir:       envOr("DOCS_DIR", "docs"),
		controllerDir: envOr("CONTROLLER_DIR", "controller"),
		clientID:      os.Getenv("GITHUB_CLIENT_ID"),
		clientSecret:  os.Getenv("GITHUB_CLIENT_SECRET"),
		baseURL:       strings.TrimSuffix(os.Getenv("BASE_URL"), "/"),
		allowedUsers:  map[string]bool{},

		aiKeys: map[string]string{
			"openai": os.Getenv("OPENAI_API_KEY"),
			"gemini": os.Getenv("GEMINI_API_KEY"),
		},
		aiModel:        os.Getenv("AI_MODEL"),
		aiVoice:        os.Getenv("AI_VOICE"),
		aiInstructions: envOr("AI_INSTRUCTIONS", defaultInstructions),
	}

	cfg.localMode = os.Getenv("MARVIN_LOCAL") == "true"

	var missing []string
	// The GitHub settings configure a sign-in that local mode does not have.
	// Demanding them would mean registering an OAuth app in order to find out
	// whether a microphone is wired the right way round.
	if !cfg.localMode {
		if cfg.clientID == "" {
			missing = append(missing, "GITHUB_CLIENT_ID")
		}
		if cfg.clientSecret == "" {
			missing = append(missing, "GITHUB_CLIENT_SECRET")
		}
	}
	// SESSION_SECRET is still required, local or not: it signs the robots'
	// credentials, and one generated afresh on every start would invalidate a
	// robot's token every time the server restarted.
	secret := os.Getenv("SESSION_SECRET")
	if secret == "" {
		missing = append(missing, "SESSION_SECRET")
	}
	if len(missing) > 0 {
		return config{}, fmt.Errorf("missing required environment: %s", strings.Join(missing, ", "))
	}
	// Not generated on the fly when absent: a per-instance key would sign
	// visitors out every time Cloud Run cold-starts or scales past one
	// instance, and the failure would look like a flaky login rather than a
	// configuration error.
	if len(secret) < minSessionKeyLen {
		return config{}, fmt.Errorf("SESSION_SECRET must be at least %d characters, got %d",
			minSessionKeyLen, len(secret))
	}
	cfg.sessionKey = []byte(secret)

	for _, u := range splitList(os.Getenv("ALLOWED_GITHUB_USERS")) {
		cfg.allowedUsers[u] = true
	}
	cfg.allowedOrgs = splitList(os.Getenv("ALLOWED_GITHUB_ORGS"))
	cfg.allowAnyUser = os.Getenv("ALLOW_ANY_GITHUB_USER") == "true"

	// Fail closed. Anyone can create a GitHub account in under a minute, so an
	// empty allowlist that defaulted to "any signed-in user" would be a public
	// site wearing a login page — exactly the outcome this server exists to
	// prevent, and one nobody would notice was broken.
	//
	// Not checked in local mode, where there is no sign-in for an allowlist to
	// qualify; localOnly is what keeps strangers out there.
	if !cfg.localMode && !cfg.allowAnyUser && len(cfg.allowedUsers) == 0 && len(cfg.allowedOrgs) == 0 {
		return config{}, errors.New(
			"no access rule set: give ALLOWED_GITHUB_USERS and/or ALLOWED_GITHUB_ORGS, " +
				"or set ALLOW_ANY_GITHUB_USER=true to let in every GitHub account")
	}

	hours := envOr("SESSION_TTL_HOURS", "12")
	n, err := strconv.Atoi(hours)
	if err != nil || n <= 0 {
		return config{}, fmt.Errorf("SESSION_TTL_HOURS must be a positive integer, got %q", hours)
	}
	cfg.sessionTTL = time.Duration(n) * time.Hour

	days := envOr("DEVICE_TOKEN_TTL_DAYS", "365")
	d, err := strconv.Atoi(days)
	if err != nil || d <= 0 {
		return config{}, fmt.Errorf("DEVICE_TOKEN_TTL_DAYS must be a positive integer, got %q", days)
	}
	cfg.deviceTokenTTL = time.Duration(d) * 24 * time.Hour

	// Default to whichever provider has a key, so that a deployment with one
	// key works without a second setting. Naming a provider with no key is a
	// startup error rather than a surprise at the first question.
	cfg.aiProvider = strings.ToLower(strings.TrimSpace(os.Getenv("AI_PROVIDER")))
	if cfg.aiProvider == "" {
		for _, name := range []string{"openai", "gemini"} {
			if cfg.aiKeys[name] != "" {
				cfg.aiProvider = name
				break
			}
		}
	}
	if cfg.aiProvider != "" && cfg.aiKeys[cfg.aiProvider] == "" {
		return config{}, fmt.Errorf("AI_PROVIDER is %q but %s is not set",
			cfg.aiProvider, apiKeyEnvFor(cfg.aiProvider))
	}

	return cfg, nil
}

// accessSummary describes the active access rule in one line, so the startup log
// makes it obvious who can get in.
func (c config) accessSummary() string {
	if c.localMode {
		return "ACCESS: local mode — no sign-in, private addresses only"
	}
	if c.allowAnyUser {
		return "ACCESS: any signed-in GitHub account"
	}
	parts := make([]string, 0, 2)
	if n := len(c.allowedUsers); n > 0 {
		parts = append(parts, fmt.Sprintf("%d user(s)", n))
	}
	if n := len(c.allowedOrgs); n > 0 {
		parts = append(parts, fmt.Sprintf("org(s): %s", strings.Join(c.allowedOrgs, ", ")))
	}
	return "access: " + strings.Join(parts, " + ")
}

// splitList parses a comma-separated environment value into lower-cased,
// de-blanked entries.
func splitList(v string) []string {
	var out []string
	for _, part := range strings.Split(v, ",") {
		if p := strings.ToLower(strings.TrimSpace(part)); p != "" {
			out = append(out, p)
		}
	}
	return out
}

func envOr(key, fallback string) string {
	if v := os.Getenv(key); v != "" {
		return v
	}
	return fallback
}

func secureEqual(a, b string) bool {
	return subtle.ConstantTimeCompare([]byte(a), []byte(b)) == 1
}

func htmlEscape(s string) string { return html.EscapeString(s) }
