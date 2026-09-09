package main

import (
	"context"
	"encoding/json"
	"fmt"
	"io"
	"log"
	"net/http"
	"net/url"
	"strings"
	"time"
)

// GitHub endpoints. Variables rather than constants so that tests can point
// them at a stub; nothing outside the tests reassigns them.
var (
	githubAuthorizeURL = "https://github.com/login/oauth/authorize"
	githubTokenURL     = "https://github.com/login/oauth/access_token"
	githubAPIURL       = "https://api.github.com"
)

const (
	loginPath    = "/auth/login"
	callbackPath = "/auth/callback"
	logoutPath   = "/auth/logout"
	signinPath   = "/signin"
)

// githubUser is the slice of GitHub's user object we actually use.
type githubUser struct {
	Login string `json:"login"`
	ID    int64  `json:"id"`
}

// handleSignin is the page an unauthenticated visitor lands on. It exists so
// that arriving at the site does not bounce a stranger straight to github.com
// with no explanation of where they are or what is being asked of them.
func (a *app) handleSignin(w http.ResponseWriter, r *http.Request) {
	ret := safeReturnPath(r.URL.Query().Get("return"))
	// Already signed in? Nothing to do here.
	if _, ok := a.currentSession(r); ok {
		http.Redirect(w, r, ret, http.StatusFound)
		return
	}
	href := loginPath + "?return=" + url.QueryEscape(ret)
	a.renderPage(w, http.StatusOK, "Sign in", `
		<h1>Marvin</h1>
		<p>This site is not public yet. Sign in with GitHub to read it.</p>
		<p><a class="button" href="`+htmlEscape(href)+`">Sign in with GitHub</a></p>
		<p class="fine">You will be asked only for your GitHub username &mdash;
		no repository access is requested.</p>`)
}

// handleLogin starts the OAuth round trip.
func (a *app) handleLogin(w http.ResponseWriter, r *http.Request) {
	nonce, err := randomToken(24)
	if err != nil {
		a.serverError(w, r, fmt.Errorf("generate nonce: %w", err))
		return
	}
	state, err := encodeState(oauthState{
		Nonce:  nonce,
		Return: safeReturnPath(r.URL.Query().Get("return")),
		Issued: time.Now().Unix(),
	}, a.cfg.sessionKey)
	if err != nil {
		a.serverError(w, r, fmt.Errorf("encode state: %w", err))
		return
	}
	setCookie(w, r, stateCookieName, nonce, int(stateTTL.Seconds()))

	q := url.Values{
		"client_id":    {a.cfg.clientID},
		"redirect_uri": {a.baseURL(r) + callbackPath},
		"state":        {state},
		"allow_signup": {"false"},
	}
	// Scope stays empty unless org membership has to be read: /user returns the
	// login and id on an unscoped token, and asking for more than that is what
	// makes people hesitate on the consent screen.
	if len(a.cfg.allowedOrgs) > 0 {
		q.Set("scope", "read:org")
	}
	http.Redirect(w, r, githubAuthorizeURL+"?"+q.Encode(), http.StatusFound)
}

// handleCallback finishes the round trip: verify, exchange, identify, admit.
func (a *app) handleCallback(w http.ResponseWriter, r *http.Request) {
	ctx, cancel := context.WithTimeout(r.Context(), 20*time.Second)
	defer cancel()

	// GitHub reports a refused consent screen here rather than by not calling.
	if e := r.URL.Query().Get("error"); e != "" {
		a.renderPage(w, http.StatusForbidden, "Sign-in cancelled", `
			<h1>Sign-in cancelled</h1>
			<p>GitHub reported: `+htmlEscape(e)+`</p>
			<p><a class="button" href="`+signinPath+`">Try again</a></p>`)
		return
	}

	cookie, err := r.Cookie(stateCookieName)
	if err != nil {
		// Usually a bookmarked callback URL or a cookie that timed out.
		a.redirectToSignin(w, r, "/")
		return
	}
	clearCookie(w, r, stateCookieName)

	state, err := decodeState(r.URL.Query().Get("state"), a.cfg.sessionKey, time.Now())
	if err != nil {
		a.badRequest(w, r, "The sign-in link has expired or was tampered with.")
		return
	}
	// The signed state proves we issued it; matching it against the cookie
	// proves this is the same browser that asked. Both halves are needed.
	if !secureEqual(state.Nonce, cookie.Value) {
		a.badRequest(w, r, "The sign-in link did not match this browser.")
		return
	}

	code := r.URL.Query().Get("code")
	if code == "" {
		a.badRequest(w, r, "GitHub did not return an authorisation code.")
		return
	}

	token, err := a.exchangeCode(ctx, code, a.baseURL(r)+callbackPath)
	if err != nil {
		a.serverError(w, r, fmt.Errorf("exchange code: %w", err))
		return
	}
	user, err := a.fetchUser(ctx, token)
	if err != nil {
		a.serverError(w, r, fmt.Errorf("fetch user: %w", err))
		return
	}

	allowed, err := a.authorize(ctx, token, user.Login)
	if err != nil {
		a.serverError(w, r, fmt.Errorf("check access for %q: %w", user.Login, err))
		return
	}
	if !allowed {
		log.Printf("access denied for github user %q (id %d)", user.Login, user.ID)
		a.renderPage(w, http.StatusForbidden, "Not on the list", `
			<h1>No access</h1>
			<p>You signed in as <strong>`+htmlEscape(user.Login)+`</strong>, which
			is not on this site's allowlist.</p>
			<p class="fine">If that is the wrong account, sign out of GitHub or
			switch accounts and try again. Otherwise ask the site owner to add
			you.</p>
			<p><a class="button" href="`+signinPath+`">Try again</a></p>`)
		return
	}

	value, err := encodeSession(session{
		Login:   user.Login,
		UserID:  user.ID,
		Expires: time.Now().Add(a.cfg.sessionTTL).Unix(),
	}, a.cfg.sessionKey)
	if err != nil {
		a.serverError(w, r, fmt.Errorf("encode session: %w", err))
		return
	}
	setCookie(w, r, sessionCookieName, value, int(a.cfg.sessionTTL.Seconds()))
	log.Printf("signed in github user %q (id %d)", user.Login, user.ID)
	http.Redirect(w, r, safeReturnPath(state.Return), http.StatusFound)
}

// handleLogout drops the session cookie. It cannot revoke anything at GitHub's
// end, and says so, because "log out" on a shared machine that leaves the
// GitHub session live is a promise this cannot keep.
func (a *app) handleLogout(w http.ResponseWriter, r *http.Request) {
	clearCookie(w, r, sessionCookieName)
	a.renderPage(w, http.StatusOK, "Signed out", `
		<h1>Signed out</h1>
		<p>Your session on this site has been cleared.</p>
		<p class="fine">You are still signed in to GitHub itself. On a shared
		computer, sign out there too.</p>
		<p><a class="button" href="`+signinPath+`">Sign in again</a></p>`)
}

// exchangeCode trades the one-time authorisation code for an access token.
func (a *app) exchangeCode(ctx context.Context, code, redirectURI string) (string, error) {
	form := url.Values{
		"client_id":     {a.cfg.clientID},
		"client_secret": {a.cfg.clientSecret},
		"code":          {code},
		"redirect_uri":  {redirectURI},
	}
	req, err := http.NewRequestWithContext(ctx, http.MethodPost, githubTokenURL,
		strings.NewReader(form.Encode()))
	if err != nil {
		return "", err
	}
	req.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	req.Header.Set("Accept", "application/json")
	req.Header.Set("User-Agent", userAgent)

	resp, err := a.hc.Do(req)
	if err != nil {
		return "", err
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return "", fmt.Errorf("github returned %s", resp.Status)
	}

	// GitHub answers a bad code with 200 and an error body, not an error status.
	var body struct {
		AccessToken      string `json:"access_token"`
		Error            string `json:"error"`
		ErrorDescription string `json:"error_description"`
	}
	if err := json.NewDecoder(io.LimitReader(resp.Body, 1<<20)).Decode(&body); err != nil {
		return "", fmt.Errorf("decode token response: %w", err)
	}
	if body.Error != "" {
		return "", fmt.Errorf("github: %s (%s)", body.Error, body.ErrorDescription)
	}
	if body.AccessToken == "" {
		return "", fmt.Errorf("github returned no access token")
	}
	return body.AccessToken, nil
}

// fetchUser identifies the token holder.
func (a *app) fetchUser(ctx context.Context, token string) (githubUser, error) {
	var user githubUser
	if err := a.githubGet(ctx, token, "/user", &user); err != nil {
		return githubUser{}, err
	}
	if user.Login == "" {
		return githubUser{}, fmt.Errorf("github returned a user with no login")
	}
	return user, nil
}

// authorize decides whether this GitHub account may read the site.
func (a *app) authorize(ctx context.Context, token, login string) (bool, error) {
	if a.cfg.allowAnyUser {
		return true, nil
	}
	// GitHub logins are case-insensitive; the allowlist is stored folded.
	if a.cfg.allowedUsers[strings.ToLower(login)] {
		return true, nil
	}
	if len(a.cfg.allowedOrgs) == 0 {
		return false, nil
	}

	var orgs []struct {
		Login string `json:"login"`
	}
	// One page of 100 covers any plausible membership list. A user in more than
	// 100 organisations would need pagination; that is not this site's problem.
	if err := a.githubGet(ctx, token, "/user/orgs?per_page=100", &orgs); err != nil {
		return false, err
	}
	member := make(map[string]bool, len(orgs))
	for _, o := range orgs {
		member[strings.ToLower(o.Login)] = true
	}
	for _, want := range a.cfg.allowedOrgs {
		if member[want] {
			return true, nil
		}
	}
	return false, nil
}

// githubGet performs an authenticated GET against the GitHub API and decodes
// the JSON body into out.
func (a *app) githubGet(ctx context.Context, token, path string, out any) error {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, githubAPIURL+path, nil)
	if err != nil {
		return err
	}
	req.Header.Set("Authorization", "Bearer "+token)
	req.Header.Set("Accept", "application/vnd.github+json")
	req.Header.Set("X-GitHub-Api-Version", "2022-11-28")
	req.Header.Set("User-Agent", userAgent)

	resp, err := a.hc.Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return fmt.Errorf("GET %s: github returned %s", path, resp.Status)
	}
	return json.NewDecoder(io.LimitReader(resp.Body, 4<<20)).Decode(out)
}
