package main

import (
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

// newTestApp builds an app serving a throwaway site directory.
func newTestApp(t *testing.T, files map[string]string) *app {
	t.Helper()
	dir := t.TempDir()
	for name, body := range files {
		path := filepath.Join(dir, name)
		if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
			t.Fatal(err)
		}
		if err := os.WriteFile(path, []byte(body), 0o644); err != nil {
			t.Fatal(err)
		}
	}
	cfg := config{
		docsDir:      dir,
		clientID:     "test-client",
		clientSecret: "test-secret",
		sessionKey:   testKey,
		sessionTTL:   time.Hour,
		allowedUsers: map[string]bool{"alice": true},
	}
	return &app{
		cfg:   cfg,
		files: http.FileServer(noListingFS{http.Dir(dir)}),
		hc:    http.DefaultClient,
	}
}

func sessionCookieFor(t *testing.T, a *app, login string) *http.Cookie {
	t.Helper()
	v, err := encodeSession(session{Login: login, UserID: 1,
		Expires: time.Now().Add(time.Hour).Unix()}, a.cfg.sessionKey)
	if err != nil {
		t.Fatal(err)
	}
	return &http.Cookie{Name: sessionCookieName, Value: v}
}

// The whole point of the server: no session, no content.
func TestSiteRequiresASession(t *testing.T) {
	// Sentinels that cannot collide with the path echoed in a redirect body.
	const homeMark, buildMark, cssMark = "SENTINEL-HOME", "SENTINEL-BUILD", "SENTINEL-CSS"
	a := newTestApp(t, map[string]string{
		"index.html":          "<h1>" + homeMark + "</h1>",
		"build.html":          "<h1>" + buildMark + "</h1>",
		"assets/css/site.css": "body{content:'" + cssMark + "'}",
	})
	h := a.routes()

	for _, path := range []string{"/", "/index.html", "/build.html", "/assets/css/site.css"} {
		rec := httptest.NewRecorder()
		h.ServeHTTP(rec, httptest.NewRequest(http.MethodGet, path, nil))

		if rec.Code != http.StatusFound {
			t.Errorf("GET %s without a session = %d, want %d", path, rec.Code, http.StatusFound)
		}
		if loc := rec.Header().Get("Location"); !strings.HasPrefix(loc, signinPath) {
			t.Errorf("GET %s redirected to %q, want the sign-in page", path, loc)
		}
		body := rec.Body.String()
		for _, mark := range []string{homeMark, buildMark, cssMark} {
			if strings.Contains(body, mark) {
				t.Errorf("GET %s leaked site content (%s) while signed out", path, mark)
			}
		}
	}
}

func TestSiteServesWithASession(t *testing.T) {
	a := newTestApp(t, map[string]string{"index.html": "<h1>SENTINEL-HOME</h1>"})
	req := httptest.NewRequest(http.MethodGet, "/", nil)
	req.AddCookie(sessionCookieFor(t, a, "alice"))

	rec := httptest.NewRecorder()
	a.routes().ServeHTTP(rec, req)

	if rec.Code != http.StatusOK {
		t.Fatalf("GET / with a session = %d, want 200", rec.Code)
	}
	if !strings.Contains(rec.Body.String(), "SENTINEL-HOME") {
		t.Error("index.html was not served")
	}
	if cc := rec.Header().Get("Cache-Control"); !strings.Contains(cc, "private") {
		t.Errorf("Cache-Control = %q, want it marked private", cc)
	}
}

// A forged or stale cookie must not loop the visitor between gate and sign-in.
func TestForgedSessionIsClearedNotLooped(t *testing.T) {
	a := newTestApp(t, map[string]string{"index.html": "<h1>SENTINEL-HOME</h1>"})
	req := httptest.NewRequest(http.MethodGet, "/", nil)
	req.AddCookie(&http.Cookie{Name: sessionCookieName, Value: "forged.token"})

	rec := httptest.NewRecorder()
	a.routes().ServeHTTP(rec, req)

	if rec.Code != http.StatusFound {
		t.Fatalf("forged cookie = %d, want a redirect", rec.Code)
	}
	var cleared bool
	for _, c := range rec.Result().Cookies() {
		if c.Name == sessionCookieName && c.MaxAge < 0 {
			cleared = true
		}
	}
	if !cleared {
		t.Error("the bad session cookie was not cleared")
	}
}

func TestHealthzIsOpen(t *testing.T) {
	a := newTestApp(t, nil)
	rec := httptest.NewRecorder()
	a.routes().ServeHTTP(rec, httptest.NewRequest(http.MethodGet, "/healthz", nil))
	if rec.Code != http.StatusOK {
		t.Errorf("GET /healthz = %d, want 200 without a session", rec.Code)
	}
}

// Directory listings would expose the asset tree to anyone who got in.
func TestNoDirectoryListing(t *testing.T) {
	a := newTestApp(t, map[string]string{
		"index.html":          "<h1>SENTINEL-HOME</h1>",
		"assets/css/site.css": "body{}",
	})
	req := httptest.NewRequest(http.MethodGet, "/assets/css/", nil)
	req.AddCookie(sessionCookieFor(t, a, "alice"))

	rec := httptest.NewRecorder()
	a.routes().ServeHTTP(rec, req)

	if rec.Code == http.StatusOK && strings.Contains(rec.Body.String(), "site.css") {
		t.Error("a directory listing was rendered")
	}
}

// The login redirect must carry the visitor's destination and reach GitHub with
// the right client id and a signed state.
func TestLoginRedirectsToGitHub(t *testing.T) {
	a := newTestApp(t, nil)
	rec := httptest.NewRecorder()
	req := httptest.NewRequest(http.MethodGet, loginPath+"?return=%2Fbuild.html", nil)
	req.Header.Set("X-Forwarded-Proto", "https")
	req.Host = "marvin.example"
	a.routes().ServeHTTP(rec, req)

	if rec.Code != http.StatusFound {
		t.Fatalf("GET %s = %d, want a redirect", loginPath, rec.Code)
	}
	loc, err := rec.Result().Location()
	if err != nil {
		t.Fatal(err)
	}
	if !strings.HasPrefix(loc.String(), githubAuthorizeURL) {
		t.Errorf("redirected to %q, want github", loc)
	}
	q := loc.Query()
	if q.Get("client_id") != "test-client" {
		t.Errorf("client_id = %q", q.Get("client_id"))
	}
	if want := "https://marvin.example" + callbackPath; q.Get("redirect_uri") != want {
		t.Errorf("redirect_uri = %q, want %q", q.Get("redirect_uri"), want)
	}
	// The unscoped request is the one that reads best on the consent screen.
	if q.Get("scope") != "" {
		t.Errorf("scope = %q, want empty when no orgs are configured", q.Get("scope"))
	}

	st, err := decodeState(q.Get("state"), a.cfg.sessionKey, time.Now())
	if err != nil {
		t.Fatalf("state parameter did not verify: %v", err)
	}
	if st.Return != "/build.html" {
		t.Errorf("state carried return %q, want /build.html", st.Return)
	}
	// The nonce must also be pinned to this browser.
	var nonce string
	for _, c := range rec.Result().Cookies() {
		if c.Name == stateCookieName {
			nonce = c.Value
		}
	}
	if nonce == "" || nonce != st.Nonce {
		t.Errorf("state cookie %q does not match state nonce %q", nonce, st.Nonce)
	}
}

func TestLoginRequestsOrgScopeWhenNeeded(t *testing.T) {
	a := newTestApp(t, nil)
	a.cfg.allowedOrgs = []string{"marvin-team"}

	rec := httptest.NewRecorder()
	a.routes().ServeHTTP(rec, httptest.NewRequest(http.MethodGet, loginPath, nil))

	loc, _ := rec.Result().Location()
	if got := loc.Query().Get("scope"); got != "read:org" {
		t.Errorf("scope = %q, want read:org when an org allowlist is set", got)
	}
}

// A state parameter with no matching cookie is the CSRF case.
func TestCallbackRejectsMismatchedState(t *testing.T) {
	a := newTestApp(t, nil)
	state, err := encodeState(oauthState{Nonce: "real-nonce", Return: "/",
		Issued: time.Now().Unix()}, a.cfg.sessionKey)
	if err != nil {
		t.Fatal(err)
	}
	req := httptest.NewRequest(http.MethodGet, callbackPath+"?code=x&state="+state, nil)
	req.AddCookie(&http.Cookie{Name: stateCookieName, Value: "someone-elses-nonce"})

	rec := httptest.NewRecorder()
	a.routes().ServeHTTP(rec, req)

	if rec.Code != http.StatusBadRequest {
		t.Errorf("mismatched state = %d, want 400", rec.Code)
	}
}

func TestLogoutClearsTheSession(t *testing.T) {
	a := newTestApp(t, nil)
	req := httptest.NewRequest(http.MethodGet, logoutPath, nil)
	req.AddCookie(sessionCookieFor(t, a, "alice"))

	rec := httptest.NewRecorder()
	a.routes().ServeHTTP(rec, req)

	for _, c := range rec.Result().Cookies() {
		if c.Name == sessionCookieName && c.MaxAge < 0 {
			return
		}
	}
	t.Error("logout did not clear the session cookie")
}

// Secure must track the scheme, or sign-in breaks on localhost.
func TestCookieSecureFlagTracksScheme(t *testing.T) {
	for _, tc := range []struct {
		name       string
		proto      string
		wantSecure bool
	}{
		{"behind the Cloud Run edge", "https", true},
		{"plain local development", "", false},
	} {
		t.Run(tc.name, func(t *testing.T) {
			rec := httptest.NewRecorder()
			req := httptest.NewRequest(http.MethodGet, "/", nil)
			if tc.proto != "" {
				req.Header.Set("X-Forwarded-Proto", tc.proto)
			}
			setCookie(rec, req, "x", "y", 60)

			c := rec.Result().Cookies()[0]
			if c.Secure != tc.wantSecure {
				t.Errorf("Secure = %v, want %v", c.Secure, tc.wantSecure)
			}
			if !c.HttpOnly {
				t.Error("HttpOnly was not set")
			}
			if c.SameSite != http.SameSiteLaxMode {
				t.Errorf("SameSite = %v, want Lax so the GitHub return hop keeps it", c.SameSite)
			}
		})
	}
}
