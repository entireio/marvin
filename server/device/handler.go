package device

import (
	"errors"
	"log"
	"net/http"
	"strings"

	"github.com/coder/websocket"

	"github.com/spedemon/marvin/server/harness"
)

// Authenticator turns a bearer token into the device it names.
//
// The implementation lives in the main package, next to the signing key that
// also secures browser sessions, so that there is one key and one signing
// routine in the server rather than two that could drift apart.
type Authenticator func(token string) (deviceID string, err error)

// ErrUnauthorized is what an Authenticator returns for a token it will not
// accept. The handler answers 401 and says nothing further: a robot cannot read
// an explanation, and an attacker should not be given one.
var ErrUnauthorized = errors.New("device: unauthorized")

// Handler serves the robot's WebSocket endpoint.
type Handler struct {
	Hub    *Hub
	Auth   Authenticator
	Config ConfigFunc
	Chain  harness.Chain
}

func (h *Handler) ServeHTTP(w http.ResponseWriter, r *http.Request) {
	deviceID, err := h.authenticate(r)
	if err != nil {
		// Logged without the token: it is a long-lived credential and belongs
		// in no log file.
		log.Printf("device: rejected connection from %s: %v", r.RemoteAddr, err)
		w.Header().Set("WWW-Authenticate", `Bearer realm="marvin"`)
		http.Error(w, "unauthorized", http.StatusUnauthorized)
		return
	}

	ws, err := websocket.Accept(w, r, &websocket.AcceptOptions{
		// Audio is PCM, which does not compress, and the robot has neither the
		// cycles nor the memory to spend finding that out fifty times a second.
		CompressionMode: websocket.CompressionDisabled,
	})
	if err != nil {
		log.Printf("device %s: accept: %v", deviceID, err)
		return
	}
	// A reply frame is 644 bytes and a control message a few hundred. Anything
	// larger is a fault or an attack.
	ws.SetReadLimit(8 << 10)

	c := &conn{
		ws:       ws,
		deviceID: deviceID,
		hub:      h.Hub,
		chain:    h.Chain,
		config:   h.Config,
	}

	// r.Context() ends when the connection does, which is what unwinds the
	// read loop, the keepalive and any conversation in flight.
	if err := c.run(r.Context()); err != nil {
		log.Printf("device %s: %v", deviceID, err)
		ws.Close(websocket.StatusInternalError, "session ended")
		return
	}
	ws.Close(websocket.StatusNormalClosure, "")
}

// authenticate reads the bearer token off the upgrade request.
//
// A header rather than a query parameter, deliberately: query strings end up in
// access logs, proxy logs and error reports, and this token is the only thing
// standing between a stranger and a live microphone in someone's home.
func (h *Handler) authenticate(r *http.Request) (string, error) {
	if h.Auth == nil {
		return "", errors.New("no authenticator configured")
	}
	header := r.Header.Get("Authorization")
	token, ok := strings.CutPrefix(header, "Bearer ")
	if !ok {
		return "", errors.New("missing bearer token")
	}
	return h.Auth(strings.TrimSpace(token))
}
