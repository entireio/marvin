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

func newLocalApp(t *testing.T) *app {
	t.Helper()
	docs := t.TempDir()
	controller := t.TempDir()
	if err := os.WriteFile(filepath.Join(docs, "index.html"), []byte("<h1>SITE</h1>"), 0o644); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(controller, "index.html"),
		[]byte("<h1>SENTINEL-CONTROLLER</h1>"), 0o644); err != nil {
		t.Fatal(err)
	}
	return newApp(config{
		docsDir:        docs,
		controllerDir:  controller,
		sessionKey:     testKey,
		sessionTTL:     time.Hour,
		localMode:      true,
		aiKeys:         map[string]string{"openai": "sk-test"},
		aiProvider:     "openai",
		deviceTokenTTL: 24 * time.Hour,
	})
}

// get issues a request with an explicit Host, which is what the local-mode
// guard decides on. httptest defaults it to example.com — a public name — so
// leaving it alone would test the opposite of what these mean to.
func get(t *testing.T, a *app, method, path, host string) *httptest.ResponseRecorder {
	t.Helper()
	req := httptest.NewRequest(method, path, strings.NewReader("{}"))
	req.Host = host
	rec := httptest.NewRecorder()
	a.routes().ServeHTTP(rec, req)
	return rec
}

// The point of the mode: a bench with no OAuth app.
func TestLocalModeServesWithoutASession(t *testing.T) {
	a := newLocalApp(t)
	for _, path := range []string{"/", "/app/", "/api/status"} {
		rec := get(t, a, http.MethodGet, path, "localhost:8080")
		if rec.Code != http.StatusOK {
			t.Errorf("%s: got %d, want 200 in local mode", path, rec.Code)
		}
	}
}

// The guard that replaces the sign-in. A request for a public hostname is not
// coming from the bench this mode exists for.
func TestLocalModeRefusesAPublicHost(t *testing.T) {
	a := newLocalApp(t)
	for _, host := range []string{
		"marvin.example.com",
		"example.com:8080",
		"8.8.8.8",
		"203.0.113.10:8080",
		"marvin.run.app",
	} {
		rec := get(t, a, http.MethodGet, "/app/", host)
		if rec.Code != http.StatusForbidden {
			t.Errorf("host %q: got %d, want 403", host, rec.Code)
		}
		if strings.Contains(rec.Body.String(), "SENTINEL-CONTROLLER") {
			t.Errorf("host %q: served the controller to a public host", host)
		}
	}
}

func TestLocalModeServesPrivateHosts(t *testing.T) {
	a := newLocalApp(t)
	for _, host := range []string{
		"localhost:8080",
		"127.0.0.1:8080",
		"[::1]:8080",
		"192.168.1.40:8080",
		"10.0.0.5:8080",
		"172.16.3.9:8080",
		"marvin.local:8080",
	} {
		if rec := get(t, a, http.MethodGet, "/api/status", host); rec.Code != http.StatusOK {
			t.Errorf("host %q: got %d, want 200", host, rec.Code)
		}
	}
}

// Dropping the sign-in must not drop the robot's credential with it. Otherwise
// anyone on the café Wi-Fi could open a microphone.
func TestLocalModeStillRequiresADeviceToken(t *testing.T) {
	a := newLocalApp(t)
	rec := get(t, a, http.MethodGet, "/v1/device", "192.168.1.40:8080")
	if rec.Code != http.StatusUnauthorized {
		t.Fatalf("got %d, want 401 — local mode must not open the robot endpoint", rec.Code)
	}
}

// The regression that matters: a deployed server must be unaffected.
func TestWithoutLocalModeASessionIsStillRequired(t *testing.T) {
	a := newVoiceApp(t, nil)
	rec := get(t, a, http.MethodGet, "/app/", "localhost:8080")
	if rec.Code != http.StatusFound {
		t.Fatalf("got %d, want a redirect to sign-in", rec.Code)
	}
	if strings.Contains(rec.Body.String(), "SENTINEL-CONTROLLER") {
		t.Error("served the controller without a session")
	}
}

func TestIsLocalHost(t *testing.T) {
	local := []string{
		"localhost", "LOCALHOST", "127.0.0.1", "::1", "[::1]",
		"192.168.0.1", "10.1.2.3", "172.20.0.1", "169.254.1.1",
		"marvin.local", "marvin.local.",
	}
	public := []string{
		"example.com", "8.8.8.8", "1.1.1.1", "203.0.113.1",
		"marvin.run.app", "", "172.32.0.1", // just outside the private range
	}
	for _, h := range local {
		if !isLocalHost(h) {
			t.Errorf("isLocalHost(%q) = false, want true", h)
		}
	}
	for _, h := range public {
		if isLocalHost(h) {
			t.Errorf("isLocalHost(%q) = true, want false", h)
		}
	}
}

// The reason deviceURL exists: the controller is open at localhost because Web
// Bluetooth will not run anywhere else without HTTPS, and localhost is the one
// address the robot certainly cannot reach.
func TestDeviceURLDoesNotOfferLocalhostToTheRobot(t *testing.T) {
	a := newLocalApp(t)

	req := httptest.NewRequest(http.MethodGet, "/api/status", nil)
	req.Host = "localhost:8080"
	got := a.deviceURL(req)

	if strings.Contains(got, "localhost") || strings.Contains(got, "127.0.0.1") {
		t.Fatalf("device URL is %q — a robot cannot reach that", got)
	}
	if !strings.HasPrefix(got, "ws://") || !strings.HasSuffix(got, "/v1/device") {
		t.Fatalf("device URL is %q, want ws://<address>/v1/device", got)
	}
}

// A request that already names a routable address is left alone.
func TestDeviceURLKeepsARoutableHost(t *testing.T) {
	a := newLocalApp(t)

	req := httptest.NewRequest(http.MethodGet, "/api/status", nil)
	req.Host = "192.168.1.40:8080"
	if got, want := a.deviceURL(req), "ws://192.168.1.40:8080/v1/device"; got != want {
		t.Fatalf("got %q, want %q", got, want)
	}
}
