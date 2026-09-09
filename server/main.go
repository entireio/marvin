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
	"syscall"
	"time"
)

const userAgent = "marvin-site/1.0 (+https://github.com/spedemon/marvin)"

// minSessionKeyLen is the shortest signing key we will start with. Below this a
// key is brute-forceable, and forging a session cookie means bypassing sign-in
// altogether.
const minSessionKeyLen = 32

type config struct {
	port         string
	docsDir      string
	clientID     string
	clientSecret string
	sessionKey   []byte
	baseURL      string // optional; empty means derive from the request
	sessionTTL   time.Duration

	allowedUsers map[string]bool // lower-cased GitHub logins
	allowedOrgs  []string        // lower-cased GitHub org logins
	allowAnyUser bool
}

type app struct {
	cfg   config
	files http.Handler
	hc    *http.Client
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

	a := &app{
		cfg:   cfg,
		files: http.FileServer(noListingFS{http.Dir(cfg.docsDir)}),
		hc:    &http.Client{Timeout: 15 * time.Second},
	}

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

	// Everything else is the website, and needs a session.
	mux.Handle("/", a.requireAuth(http.HandlerFunc(a.serveSite)))

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
		if _, ok := a.currentSession(r); !ok {
			a.redirectToSignin(w, r, r.URL.RequestURI())
			return
		}
		next.ServeHTTP(w, r)
	})
}

func (a *app) currentSession(r *http.Request) (session, bool) {
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
		port:         envOr("PORT", "8080"),
		docsDir:      envOr("DOCS_DIR", "docs"),
		clientID:     os.Getenv("GITHUB_CLIENT_ID"),
		clientSecret: os.Getenv("GITHUB_CLIENT_SECRET"),
		baseURL:      strings.TrimSuffix(os.Getenv("BASE_URL"), "/"),
		allowedUsers: map[string]bool{},
	}

	var missing []string
	if cfg.clientID == "" {
		missing = append(missing, "GITHUB_CLIENT_ID")
	}
	if cfg.clientSecret == "" {
		missing = append(missing, "GITHUB_CLIENT_SECRET")
	}
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
	if !cfg.allowAnyUser && len(cfg.allowedUsers) == 0 && len(cfg.allowedOrgs) == 0 {
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

	return cfg, nil
}

// accessSummary describes the active access rule in one line, so the startup log
// makes it obvious who can get in.
func (c config) accessSummary() string {
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
