package main

import (
	"fmt"
	"log"
	"net/http"
)

// The sign-in and error pages are the only HTML this server generates. They are
// intentionally self-contained — no asset requests — because every asset on this
// site sits behind the very gate these pages are explaining. They borrow the
// site's typeface and its light/dark behaviour so the boundary does not feel
// like a different product.
const pageTemplate = `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="robots" content="noindex, nofollow">
<title>%s &mdash; Marvin</title>
<link rel="icon" href="data:,">
<style>
  :root {
    color-scheme: light dark;
    --bg: #f4f4f2; --fg: #1a1a18; --muted: #6b6b66;
    --line: #d8d8d4; --accent: #1a1a18; --accent-fg: #f4f4f2;
  }
  @media (prefers-color-scheme: dark) {
    :root {
      --bg: #131312; --fg: #e8e8e4; --muted: #93938c;
      --line: #2e2e2b; --accent: #e8e8e4; --accent-fg: #131312;
    }
  }
  * { box-sizing: border-box; }
  body {
    margin: 0; min-height: 100vh; display: grid; place-items: center;
    padding: 2rem 1.5rem; background: var(--bg); color: var(--fg);
    font: 400 16px/1.55 "Barlow", ui-sans-serif, system-ui, -apple-system, sans-serif;
  }
  main { width: 100%%; max-width: 26rem; }
  h1 {
    margin: 0 0 1rem; font-size: 1.75rem; line-height: 1.1;
    letter-spacing: -0.01em; text-transform: uppercase;
  }
  p { margin: 0 0 1rem; }
  strong { font-weight: 600; }
  .fine { color: var(--muted); font-size: 0.875rem; }
  .button {
    display: inline-block; margin: 0.5rem 0; padding: 0.7rem 1.25rem;
    background: var(--accent); color: var(--accent-fg); text-decoration: none;
    font-weight: 600; letter-spacing: 0.02em; text-transform: uppercase;
    font-size: 0.875rem; border: 1px solid var(--accent);
  }
  .button:hover { background: transparent; color: var(--fg); }
  .button:focus-visible { outline: 2px solid var(--fg); outline-offset: 3px; }
  hr { border: 0; border-top: 1px solid var(--line); margin: 2rem 0 1rem; }
</style>
</head>
<body><main>%s</main></body>
</html>
`

// renderPage writes one of those pages. body is trusted markup assembled by this
// package; anything interpolated into it must already have gone through
// htmlEscape at the call site.
func (a *app) renderPage(w http.ResponseWriter, status int, title, body string) {
	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	w.Header().Set("Cache-Control", "no-store")
	w.WriteHeader(status)
	fmt.Fprintf(w, pageTemplate, htmlEscape(title), body)
}

// serverError logs the real cause and shows the visitor a page that does not
// repeat it. Sign-in failures quote GitHub API responses, which is exactly the
// kind of detail that should stay in the logs.
func (a *app) serverError(w http.ResponseWriter, r *http.Request, err error) {
	log.Printf("error: %s %s: %v", r.Method, r.URL.Path, err)
	a.renderPage(w, http.StatusInternalServerError, "Something went wrong", `
		<h1>Something went wrong</h1>
		<p>The sign-in could not be completed. The details are in the server log.</p>
		<p><a class="button" href="`+signinPath+`">Try again</a></p>`)
}

func (a *app) badRequest(w http.ResponseWriter, r *http.Request, reason string) {
	log.Printf("rejected: %s %s: %s", r.Method, r.URL.Path, reason)
	a.renderPage(w, http.StatusBadRequest, "Sign-in failed", `
		<h1>Sign-in failed</h1>
		<p>`+htmlEscape(reason)+`</p>
		<p><a class="button" href="`+signinPath+`">Start again</a></p>`)
}
