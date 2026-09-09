package main

import (
	"context"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"os"
	"strings"
	"testing"
	"time"
)

var testKey = []byte("test-key-that-is-long-enough-to-pass-32")

func TestSignRoundTrip(t *testing.T) {
	token := sign([]byte(`{"hello":"world"}`), testKey)
	got, err := unsign(token, testKey)
	if err != nil {
		t.Fatalf("unsign: %v", err)
	}
	if string(got) != `{"hello":"world"}` {
		t.Errorf("payload = %q, want the original", got)
	}
}

func TestUnsignRejectsTampering(t *testing.T) {
	valid := sign([]byte(`{"login":"alice"}`), testKey)
	forged := sign([]byte(`{"login":"alice"}`), []byte("a-different-key-of-sufficient-len"))
	body, sig, _ := strings.Cut(valid, ".")

	for name, token := range map[string]string{
		"empty":            "",
		"no separator":     body + sig,
		"payload swapped":  sign([]byte(`{"login":"mallory"}`), testKey[:8]),
		"signed elsewhere": forged,
		"signature cut":    body + "." + sig[:len(sig)-2],
		"payload mutated":  strings.Replace(body, body[:4], "AAAA", 1) + "." + sig,
	} {
		if _, err := unsign(token, testKey); err == nil {
			t.Errorf("%s: accepted a token it should have rejected", name)
		}
	}
}

func TestSessionExpiry(t *testing.T) {
	now := time.Unix(1_700_000_000, 0)
	token, err := encodeSession(session{Login: "alice", UserID: 7,
		Expires: now.Add(time.Hour).Unix()}, testKey)
	if err != nil {
		t.Fatalf("encode: %v", err)
	}

	s, err := decodeSession(token, testKey, now)
	if err != nil {
		t.Fatalf("decode inside window: %v", err)
	}
	if s.Login != "alice" || s.UserID != 7 {
		t.Errorf("decoded %+v, want alice/7", s)
	}
	if _, err := decodeSession(token, testKey, now.Add(2*time.Hour)); err == nil {
		t.Error("accepted an expired session")
	}
}

func TestStateExpiry(t *testing.T) {
	now := time.Unix(1_700_000_000, 0)
	token, err := encodeState(oauthState{Nonce: "n", Return: "/build.html",
		Issued: now.Unix()}, testKey)
	if err != nil {
		t.Fatalf("encode: %v", err)
	}
	if _, err := decodeState(token, testKey, now.Add(time.Minute)); err != nil {
		t.Fatalf("decode inside window: %v", err)
	}
	if _, err := decodeState(token, testKey, now.Add(stateTTL+time.Second)); err == nil {
		t.Error("accepted an expired state")
	}
}

// The open-redirect surface. Each of these has been a real bypass somewhere.
func TestSafeReturnPath(t *testing.T) {
	for input, want := range map[string]string{
		"/build.html":           "/build.html",
		"/assets/css/site.css":  "/assets/css/site.css",
		"/reference.html?a=1":   "/reference.html?a=1",
		"":                      "/",
		"//evil.example":        "/",
		"///evil.example":       "/",
		"https://evil.example":  "/",
		"http://evil.example":   "/",
		"//evil.example/a":      "/",
		"/\\evil.example":       "/",
		"\\\\evil.example":      "/",
		"javascript:alert(1)":   "/",
		"/x\r\nSet-Cookie: a=b": "/",
		"build.html":            "/",
		"../../etc/passwd":      "/",
	} {
		if got := safeReturnPath(input); got != want {
			t.Errorf("safeReturnPath(%q) = %q, want %q", input, got, want)
		}
	}
}

func TestAuthorize(t *testing.T) {
	// Stub GitHub: alice is in "marvin-team", bob is in nothing.
	stub := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/user/orgs" {
			http.NotFound(w, r)
			return
		}
		orgs := []map[string]string{}
		if r.Header.Get("Authorization") == "Bearer alice-token" {
			orgs = append(orgs, map[string]string{"login": "Marvin-Team"})
		}
		json.NewEncoder(w).Encode(orgs)
	}))
	defer stub.Close()
	githubAPIURL = stub.URL
	defer func() { githubAPIURL = "https://api.github.com" }()

	tests := []struct {
		name  string
		cfg   config
		token string
		login string
		want  bool
	}{
		{"any user allowed", config{allowAnyUser: true}, "", "stranger", true},
		{"on the user list", config{allowedUsers: map[string]bool{"alice": true}}, "", "alice", true},
		{"login case folded", config{allowedUsers: map[string]bool{"alice": true}}, "", "ALICE", true},
		{"not on the user list", config{allowedUsers: map[string]bool{"alice": true}}, "", "mallory", false},
		{"in an allowed org", config{allowedOrgs: []string{"marvin-team"}}, "alice-token", "alice", true},
		{"not in the org", config{allowedOrgs: []string{"marvin-team"}}, "bob-token", "bob", false},
		{"user list misses, org saves", config{
			allowedUsers: map[string]bool{"carol": true},
			allowedOrgs:  []string{"marvin-team"},
		}, "alice-token", "alice", true},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			if tc.cfg.allowedUsers == nil {
				tc.cfg.allowedUsers = map[string]bool{}
			}
			a := &app{cfg: tc.cfg, hc: stub.Client()}
			got, err := a.authorize(context.Background(), tc.token, tc.login)
			if err != nil {
				t.Fatalf("authorize: %v", err)
			}
			if got != tc.want {
				t.Errorf("authorize(%q) = %v, want %v", tc.login, got, tc.want)
			}
		})
	}
}

// loadConfig must refuse to start a server that would admit the whole internet.
func TestLoadConfigFailsClosed(t *testing.T) {
	base := map[string]string{
		"GITHUB_CLIENT_ID":     "id",
		"GITHUB_CLIENT_SECRET": "secret",
		"SESSION_SECRET":       string(testKey),
	}
	setenv := func(t *testing.T, extra map[string]string) {
		for _, k := range []string{"GITHUB_CLIENT_ID", "GITHUB_CLIENT_SECRET", "SESSION_SECRET",
			"ALLOWED_GITHUB_USERS", "ALLOWED_GITHUB_ORGS", "ALLOW_ANY_GITHUB_USER",
			"SESSION_TTL_HOURS", "BASE_URL", "DOCS_DIR", "PORT"} {
			os.Unsetenv(k)
		}
		for k, v := range base {
			t.Setenv(k, v)
		}
		for k, v := range extra {
			t.Setenv(k, v)
		}
	}

	t.Run("no access rule is an error", func(t *testing.T) {
		setenv(t, nil)
		if _, err := loadConfig(); err == nil {
			t.Fatal("started with no allowlist and no explicit opt-in")
		}
	})
	t.Run("short session secret is an error", func(t *testing.T) {
		setenv(t, map[string]string{"SESSION_SECRET": "tooshort", "ALLOWED_GITHUB_USERS": "alice"})
		if _, err := loadConfig(); err == nil {
			t.Fatal("accepted a brute-forceable signing key")
		}
	})
	t.Run("missing client id is an error", func(t *testing.T) {
		setenv(t, map[string]string{"GITHUB_CLIENT_ID": "", "ALLOWED_GITHUB_USERS": "alice"})
		if _, err := loadConfig(); err == nil {
			t.Fatal("accepted a missing GITHUB_CLIENT_ID")
		}
	})
	t.Run("user list is parsed and folded", func(t *testing.T) {
		setenv(t, map[string]string{"ALLOWED_GITHUB_USERS": " Alice , BOB ,, "})
		cfg, err := loadConfig()
		if err != nil {
			t.Fatalf("loadConfig: %v", err)
		}
		if !cfg.allowedUsers["alice"] || !cfg.allowedUsers["bob"] || len(cfg.allowedUsers) != 2 {
			t.Errorf("allowedUsers = %v, want exactly alice and bob", cfg.allowedUsers)
		}
	})
	t.Run("explicit opt-in is honoured", func(t *testing.T) {
		setenv(t, map[string]string{"ALLOW_ANY_GITHUB_USER": "true"})
		cfg, err := loadConfig()
		if err != nil {
			t.Fatalf("loadConfig: %v", err)
		}
		if !cfg.allowAnyUser {
			t.Error("ALLOW_ANY_GITHUB_USER=true was not honoured")
		}
	})
}
