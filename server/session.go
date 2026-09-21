package main

import (
	"crypto/rand"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"net/url"
	"strings"
	"time"

	"github.com/spedemon/marvin/server/internal/token"
)

const (
	sessionCookieName = "marvin_session"
	stateCookieName   = "marvin_oauth_state"

	// stateTTL bounds how long a half-finished sign-in stays valid. Long enough
	// to type a password and clear 2FA, short enough that a captured state
	// parameter is stale by the time anyone could reuse it.
	stateTTL = 15 * time.Minute
)

// session is what we remember about a signed-in visitor. It is deliberately
// small: it rides in a cookie on every single request, assets included.
type session struct {
	Login   string `json:"login"`
	UserID  int64  `json:"uid"`
	Expires int64  `json:"exp"` // Unix seconds
}

// oauthState is the CSRF token for the OAuth round trip. It travels two ways at
// once: signed, in the `state` query parameter GitHub echoes back, and as a
// bare nonce in a cookie. Checking that the two agree proves the browser
// finishing the flow is the one that started it — an attacker can replay a
// stolen state parameter but cannot produce the matching cookie.
type oauthState struct {
	Nonce  string `json:"n"`
	Return string `json:"r"`
	Issued int64  `json:"iat"`
}

// Browser sessions and robot credentials are signed the same way with the same
// key; internal/token holds the one implementation, so that the two can never
// drift apart into one that is careful and one that is not.
func sign(payload, key []byte) string               { return token.Sign(payload, key) }
func unsign(tok string, key []byte) ([]byte, error) { return token.Unsign(tok, key) }

func encodeSession(s session, key []byte) (string, error) {
	payload, err := json.Marshal(s)
	if err != nil {
		return "", err
	}
	return sign(payload, key), nil
}

func decodeSession(token string, key []byte, now time.Time) (session, error) {
	payload, err := unsign(token, key)
	if err != nil {
		return session{}, err
	}
	var s session
	if err := json.Unmarshal(payload, &s); err != nil {
		return session{}, err
	}
	if s.Login == "" {
		return session{}, errors.New("session has no login")
	}
	if now.Unix() >= s.Expires {
		return session{}, errors.New("session expired")
	}
	return s, nil
}

func encodeState(s oauthState, key []byte) (string, error) {
	payload, err := json.Marshal(s)
	if err != nil {
		return "", err
	}
	return sign(payload, key), nil
}

func decodeState(token string, key []byte, now time.Time) (oauthState, error) {
	payload, err := unsign(token, key)
	if err != nil {
		return oauthState{}, err
	}
	var s oauthState
	if err := json.Unmarshal(payload, &s); err != nil {
		return oauthState{}, err
	}
	if s.Nonce == "" {
		return oauthState{}, errors.New("state has no nonce")
	}
	if now.After(time.Unix(s.Issued, 0).Add(stateTTL)) {
		return oauthState{}, errors.New("state expired")
	}
	return s, nil
}

// randomToken returns n bytes of cryptographic randomness, base64url-encoded.
func randomToken(n int) (string, error) {
	b := make([]byte, n)
	if _, err := rand.Read(b); err != nil {
		return "", fmt.Errorf("read random: %w", err)
	}
	return base64.RawURLEncoding.EncodeToString(b), nil
}

// safeReturnPath keeps the post-login redirect pointed at this site. Anything
// that could leave the origin collapses to "/": an absolute URL, a
// scheme-relative "//evil.example", a backslash that some browsers normalise
// into a slash, or a header-splitting newline. Without this the sign-in
// endpoint is an open redirect, and an open redirect on the host that issues
// the session cookie is worth a great deal to a phisher.
func safeReturnPath(p string) string {
	if p == "" || p[0] != '/' {
		return "/"
	}
	if strings.HasPrefix(p, "//") || strings.ContainsAny(p, "\\\r\n") {
		return "/"
	}
	u, err := url.Parse(p)
	if err != nil || u.Scheme != "" || u.Host != "" {
		return "/"
	}
	return u.RequestURI()
}

// isHTTPS reports whether the visitor's own connection is TLS. Cloud Run
// terminates TLS at the edge and forwards plain HTTP to the container, so the
// forwarded header is the real signal in production; r.TLS is only ever set
// when the binary is run directly with a certificate.
func isHTTPS(r *http.Request) bool {
	if r.TLS != nil {
		return true
	}
	proto := r.Header.Get("X-Forwarded-Proto")
	if i := strings.IndexByte(proto, ','); i >= 0 {
		proto = proto[:i] // a proxy chain appends; the first hop is the client's
	}
	return strings.EqualFold(strings.TrimSpace(proto), "https")
}

// setCookie writes a cookie hardened the same way every time. Secure tracks the
// actual scheme so that local development over http still works — a Secure
// cookie on http is simply dropped, which would make sign-in fail silently.
func setCookie(w http.ResponseWriter, r *http.Request, name, value string, maxAge int) {
	http.SetCookie(w, &http.Cookie{
		Name:     name,
		Value:    value,
		Path:     "/",
		MaxAge:   maxAge,
		HttpOnly: true,
		Secure:   isHTTPS(r),
		// Lax rather than Strict: GitHub returns the visitor with a top-level
		// GET from github.com, and Strict withholds cookies on that hop, which
		// would break the state check on every sign-in.
		SameSite: http.SameSiteLaxMode,
	})
}

func clearCookie(w http.ResponseWriter, r *http.Request, name string) {
	setCookie(w, r, name, "", -1)
}
