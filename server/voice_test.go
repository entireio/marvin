package main

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

// newVoiceApp builds a fully wired server, voice included, over throwaway
// directories.
func newVoiceApp(t *testing.T, keys map[string]string) *app {
	t.Helper()
	docs := t.TempDir()
	controller := t.TempDir()
	if err := os.WriteFile(filepath.Join(docs, "index.html"), []byte("<h1>SITE</h1>"), 0o644); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(controller, "index.html"), []byte("<h1>SENTINEL-CONTROLLER</h1>"), 0o644); err != nil {
		t.Fatal(err)
	}
	if keys == nil {
		keys = map[string]string{"openai": "sk-test"}
	}
	provider := ""
	for _, name := range []string{"openai", "gemini"} {
		if keys[name] != "" {
			provider = name
			break
		}
	}
	return newApp(config{
		docsDir:        docs,
		controllerDir:  controller,
		clientID:       "test-client",
		clientSecret:   "test-secret",
		sessionKey:     testKey,
		sessionTTL:     time.Hour,
		allowedUsers:   map[string]bool{"alice": true},
		aiKeys:         keys,
		aiProvider:     provider,
		deviceTokenTTL: 24 * time.Hour,
	})
}

// Adding the controller and its API must not have opened a way past the
// sign-in that the whole server exists to enforce.
func TestVoiceRoutesRequireASession(t *testing.T) {
	a := newVoiceApp(t, nil)
	h := a.routes()

	for _, tc := range []struct{ method, path string }{
		{http.MethodGet, "/api/status"},
		{http.MethodPost, "/api/provider"},
		{http.MethodPost, "/api/device-token"},
		{http.MethodGet, "/ws/controller"},
		{http.MethodGet, "/app/"},
		{http.MethodGet, "/app/index.html"},
	} {
		rec := httptest.NewRecorder()
		h.ServeHTTP(rec, httptest.NewRequest(tc.method, tc.path, strings.NewReader("{}")))

		if rec.Code != http.StatusFound {
			t.Errorf("%s %s: got %d, want a redirect to sign-in", tc.method, tc.path, rec.Code)
		}
		if strings.Contains(rec.Body.String(), "SENTINEL-CONTROLLER") {
			t.Errorf("%s %s: served the controller to a signed-out visitor", tc.method, tc.path)
		}
	}
}

// The robot is not a browser. Answering it with a redirect to a GitHub sign-in
// page would leave it retrying an HTML document forever.
func TestDeviceEndpointAnswers401NotARedirect(t *testing.T) {
	a := newVoiceApp(t, nil)
	rec := httptest.NewRecorder()
	a.routes().ServeHTTP(rec, httptest.NewRequest(http.MethodGet, "/v1/device", nil))

	if rec.Code != http.StatusUnauthorized {
		t.Fatalf("got %d, want 401", rec.Code)
	}
	if got := rec.Header().Get("WWW-Authenticate"); !strings.Contains(got, "Bearer") {
		t.Errorf("WWW-Authenticate was %q, expected it to ask for a bearer token", got)
	}
}

func TestStatusReportsWhichProvidersAreUsable(t *testing.T) {
	a := newVoiceApp(t, map[string]string{"gemini": "key-here"})

	rec := httptest.NewRecorder()
	req := httptest.NewRequest(http.MethodGet, "/api/status", nil)
	req.AddCookie(sessionCookieFor(t, a, "alice"))
	a.routes().ServeHTTP(rec, req)

	if rec.Code != http.StatusOK {
		t.Fatalf("got %d, want 200", rec.Code)
	}
	var got statusResponse
	if err := json.Unmarshal(rec.Body.Bytes(), &got); err != nil {
		t.Fatal(err)
	}
	if got.WakeWord != wakeWord {
		t.Errorf("wake word reported as %q, want %q", got.WakeWord, wakeWord)
	}

	byName := map[string]providerStatus{}
	for _, p := range got.Providers {
		byName[p.Name] = p
	}
	if !byName["gemini"].Configured || !byName["gemini"].Active {
		t.Errorf("gemini has the only key but is %+v", byName["gemini"])
	}
	if byName["openai"].Configured {
		t.Error("openai reported as configured with no key set")
	}
	if byName["openai"].DefaultModel == "" {
		t.Error("an unconfigured provider should still report its default model")
	}
}

// Switching to a provider with no key would produce a robot that fails at the
// next question rather than at the moment of the mistake.
func TestSelectingAnUnconfiguredProviderIsRefused(t *testing.T) {
	a := newVoiceApp(t, map[string]string{"openai": "sk-test"})

	rec := httptest.NewRecorder()
	req := httptest.NewRequest(http.MethodPost, "/api/provider",
		strings.NewReader(`{"provider":"gemini"}`))
	req.AddCookie(sessionCookieFor(t, a, "alice"))
	a.routes().ServeHTTP(rec, req)

	if rec.Code != http.StatusBadRequest {
		t.Fatalf("got %d, want 400", rec.Code)
	}
	if !strings.Contains(rec.Body.String(), "GEMINI_API_KEY") {
		t.Errorf("the error should name the variable to set; got %s", rec.Body.String())
	}
	if a.active() != "openai" {
		t.Errorf("active provider changed to %q despite the refusal", a.active())
	}
}

func TestDeviceTokenEndpointIssuesAUsableToken(t *testing.T) {
	a := newVoiceApp(t, nil)

	rec := httptest.NewRecorder()
	req := httptest.NewRequest(http.MethodPost, "/api/device-token",
		strings.NewReader(`{"device_id":"marvin-kitchen"}`))
	req.AddCookie(sessionCookieFor(t, a, "alice"))
	a.routes().ServeHTTP(rec, req)

	if rec.Code != http.StatusOK {
		t.Fatalf("got %d: %s", rec.Code, rec.Body.String())
	}
	var got struct {
		DeviceID string `json:"device_id"`
		Token    string `json:"token"`
	}
	if err := json.Unmarshal(rec.Body.Bytes(), &got); err != nil {
		t.Fatal(err)
	}
	// The token has to authenticate against the very endpoint it was minted for.
	id, err := a.authenticateDevice(got.Token)
	if err != nil {
		t.Fatalf("the server would not accept a token it issued: %v", err)
	}
	if id != "marvin-kitchen" {
		t.Errorf("token names %q, want marvin-kitchen", id)
	}
}

func TestDeviceTokenEndpointRejectsAnUnsafeName(t *testing.T) {
	a := newVoiceApp(t, nil)

	rec := httptest.NewRecorder()
	req := httptest.NewRequest(http.MethodPost, "/api/device-token",
		strings.NewReader(`{"device_id":"../../etc/passwd"}`))
	req.AddCookie(sessionCookieFor(t, a, "alice"))
	a.routes().ServeHTTP(rec, req)

	if rec.Code != http.StatusBadRequest {
		t.Fatalf("got %d, want 400", rec.Code)
	}
}

// The listening button in the controller drives this. On a board with no wake
// word it is the only way to start a conversation at all, so it is worth more
// than a smoke test.

func TestDeviceActionRefusesADisconnectedRobot(t *testing.T) {
	a := newVoiceApp(t, nil)

	rec := httptest.NewRecorder()
	req := httptest.NewRequest(http.MethodPost, "/api/device-action",
		strings.NewReader(`{"device_id":"marvin","cmd":"W1"}`))
	req.AddCookie(sessionCookieFor(t, a, "alice"))
	a.routes().ServeHTTP(rec, req)

	// Not an error in the server — the robot is simply switched off — so the
	// controller can say so plainly rather than showing a failure.
	if rec.Code != http.StatusConflict {
		t.Fatalf("got %d, want 409", rec.Code)
	}
	if !strings.Contains(rec.Body.String(), "not connected") {
		t.Errorf("the message should say the robot is not connected; got %s", rec.Body.String())
	}
}

func TestDeviceActionValidatesItsInput(t *testing.T) {
	a := newVoiceApp(t, nil)

	for name, body := range map[string]string{
		"no device":       `{"cmd":"W1"}`,
		"unsafe id":       `{"device_id":"../etc","cmd":"W1"}`,
		"nothing to do":   `{"device_id":"marvin"}`,
		"overlong cmd":    `{"device_id":"marvin","cmd":"` + strings.Repeat("W", 40) + `"}`,
		"not json at all": `nonsense`,
	} {
		rec := httptest.NewRecorder()
		req := httptest.NewRequest(http.MethodPost, "/api/device-action", strings.NewReader(body))
		req.AddCookie(sessionCookieFor(t, a, "alice"))
		a.routes().ServeHTTP(rec, req)

		if rec.Code != http.StatusBadRequest {
			t.Errorf("%s: got %d, want 400", name, rec.Code)
		}
	}
}

func TestDeviceActionRequiresASession(t *testing.T) {
	a := newVoiceApp(t, nil)

	rec := httptest.NewRecorder()
	req := httptest.NewRequest(http.MethodPost, "/api/device-action",
		strings.NewReader(`{"device_id":"marvin","cmd":"W1"}`))
	a.routes().ServeHTTP(rec, req)

	if rec.Code != http.StatusFound {
		t.Fatalf("got %d, want a redirect to sign-in — this endpoint drives a robot", rec.Code)
	}
}
