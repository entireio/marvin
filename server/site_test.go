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

// These run against the real docs/ tree rather than a fixture, so that the gate
// is proven over the actual site: the pages people would read, the CSS, and the
// 7 MB video that is the most tempting thing to leave unprotected.
func realSiteApp(t *testing.T) *app {
	t.Helper()
	dir, err := filepath.Abs("../docs")
	if err != nil {
		t.Fatal(err)
	}
	if _, err := os.Stat(filepath.Join(dir, "index.html")); err != nil {
		t.Skipf("no built site at %s: %v", dir, err)
	}
	cfg := config{
		docsDir:      dir,
		clientID:     "test-client",
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

func TestRealSiteIsGated(t *testing.T) {
	a := realSiteApp(t)
	h := a.routes()

	paths := []string{
		"/", "/index.html", "/build.html", "/drive.html",
		"/contribute.html", "/reference.html",
		"/assets/css/site.css", "/assets/css/industry.css",
		"/assets/js/site.js", "/assets/img/favicon.svg",
		"/assets/video/marvin_head_7sec_pingpong.mp4",
		"/DESIGN-BRIEF.md", "/README.md",
	}
	for _, p := range paths {
		t.Run("signed out "+p, func(t *testing.T) {
			rec := httptest.NewRecorder()
			h.ServeHTTP(rec, httptest.NewRequest(http.MethodGet, p, nil))

			if rec.Code != http.StatusFound {
				t.Fatalf("status %d, want a redirect to sign-in", rec.Code)
			}
			if loc := rec.Header().Get("Location"); !strings.HasPrefix(loc, signinPath) {
				t.Fatalf("redirected to %q", loc)
			}
			// A redirect body is a one-line stub; anything larger means a real
			// file was written to the response.
			if rec.Body.Len() > 512 {
				t.Fatalf("responded with %d bytes while signed out", rec.Body.Len())
			}
		})
	}
}

func TestRealSiteServesToASignedInVisitor(t *testing.T) {
	a := realSiteApp(t)
	h := a.routes()
	cookie := sessionCookieFor(t, a, "alice")

	for _, tc := range []struct {
		path, wantType, wantBody string
	}{
		{"/", "text/html", "industry.css"},
		{"/build.html", "text/html", "<html"},
		{"/assets/css/industry.css", "text/css", "--color"},
		{"/assets/js/site.js", "javascript", "data-action"},
	} {
		t.Run(tc.path, func(t *testing.T) {
			req := httptest.NewRequest(http.MethodGet, tc.path, nil)
			req.AddCookie(cookie)
			rec := httptest.NewRecorder()
			h.ServeHTTP(rec, req)

			if rec.Code != http.StatusOK {
				t.Fatalf("status %d, want 200", rec.Code)
			}
			if ct := rec.Header().Get("Content-Type"); !strings.Contains(ct, tc.wantType) {
				t.Errorf("Content-Type %q, want it to contain %q", ct, tc.wantType)
			}
			if !strings.Contains(rec.Body.String(), tc.wantBody) {
				t.Errorf("body does not contain %q — wrong file served?", tc.wantBody)
			}
		})
	}
}

// http.FileServer canonicalises /index.html to /. Worth pinning: it means the
// site has one URL per page rather than two, and the redirect happens after the
// gate, not before it.
func TestRealSiteCanonicalisesIndex(t *testing.T) {
	a := realSiteApp(t)
	req := httptest.NewRequest(http.MethodGet, "/index.html", nil)
	req.AddCookie(sessionCookieFor(t, a, "alice"))

	rec := httptest.NewRecorder()
	a.routes().ServeHTTP(rec, req)

	if rec.Code != http.StatusMovedPermanently {
		t.Fatalf("status %d, want 301", rec.Code)
	}
	if loc := rec.Header().Get("Location"); loc != "./" {
		t.Errorf("Location %q, want ./", loc)
	}
}

// The asset directories have no index.html, so they are the listing risk.
func TestRealSiteHidesAssetListings(t *testing.T) {
	a := realSiteApp(t)
	h := a.routes()
	cookie := sessionCookieFor(t, a, "alice")

	for _, p := range []string{"/assets/", "/assets/css/", "/assets/js/", "/assets/video/"} {
		req := httptest.NewRequest(http.MethodGet, p, nil)
		req.AddCookie(cookie)
		rec := httptest.NewRecorder()
		h.ServeHTTP(rec, req)

		if rec.Code == http.StatusOK {
			t.Errorf("GET %s returned a listing (200)", p)
		}
	}
}

// Range requests are what a browser uses to scrub the demo video; the gate must
// not break them for a signed-in visitor.
func TestRealSiteSupportsRangeRequests(t *testing.T) {
	a := realSiteApp(t)
	req := httptest.NewRequest(http.MethodGet, "/assets/video/marvin_head_7sec_pingpong.mp4", nil)
	req.AddCookie(sessionCookieFor(t, a, "alice"))
	req.Header.Set("Range", "bytes=0-1023")

	rec := httptest.NewRecorder()
	a.routes().ServeHTTP(rec, req)

	if rec.Code != http.StatusPartialContent {
		t.Fatalf("status %d, want 206", rec.Code)
	}
	if got := rec.Body.Len(); got != 1024 {
		t.Errorf("returned %d bytes, want 1024", got)
	}
}
