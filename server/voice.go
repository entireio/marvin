package main

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"log"
	"net/http"
	"time"

	"github.com/coder/websocket"

	"github.com/spedemon/marvin/server/device"
	"github.com/spedemon/marvin/server/harness"
	"github.com/spedemon/marvin/server/internal/token"
	"github.com/spedemon/marvin/server/protocol"
	"github.com/spedemon/marvin/server/provider"
)

// The voice half of the server: the robot's endpoint, the controller's API, and
// the glue that decides which AI service a conversation goes to.

// defaultInstructions is Marvin's system prompt.
//
// The length instruction is the load-bearing part. Every word here is spoken
// aloud through a small speaker with no screen to fall back on, so a reply that
// would be a good paragraph on a webpage is a bad forty seconds in a room.
// wakeWord is the phrase the firmware listens for. Reported by the status
// endpoint so the web controller can tell someone what to say without the two
// being able to drift apart silently.
const wakeWord = "Hey Marvin"

const defaultInstructions = `You are Marvin, a small tracked robot with an expressive head.
You are helpful, warm and dry-humoured, and you never pretend to be a person.

Your replies are spoken aloud through a small speaker, so keep them short —
usually one or two sentences. Give the answer first and stop; offer detail only
if you are asked for it. Do not read out lists, URLs or long numbers unless
someone specifically asks. If you did not catch something, say so plainly and
ask them to repeat it.`

// setupVoice builds the parts of the server that the robot talks to. It is
// called once at startup; the pieces it creates are then shared by every
// request.
func (a *app) setupVoice() {
	a.hub = device.NewHub()
	a.providers = provider.NewSet(provider.NewOpenAI(), provider.NewGemini())

	// One handler today. The chain exists so that detecting an intent and
	// acting on it later is an addition here rather than a change to the audio
	// path.
	a.chain = harness.Chain{harness.PassThrough{}}

	a.activeProvider.Store(&a.cfg.aiProvider)
	log.Printf("voice: providers configured: %v; active: %s; handlers: %v",
		a.configuredProviders(), a.cfg.aiProvider, a.chain.Names())
}

// configuredProviders lists the providers this deployment actually has a key
// for, in registration order.
func (a *app) configuredProviders() []string {
	var out []string
	for _, p := range a.providers.All() {
		if a.cfg.aiKeys[p.Name()] != "" {
			out = append(out, p.Name())
		}
	}
	return out
}

func (a *app) active() string {
	if p := a.activeProvider.Load(); p != nil {
		return *p
	}
	return a.cfg.aiProvider
}

// resolveProvider picks the service for a conversation that is starting now.
//
// Resolving per turn rather than per connection is what lets a provider change
// in the web controller take effect on the next question. A robot holds its
// connection open for as long as it has power, so anything decided at connect
// time would need a power cycle to change.
func (a *app) resolveProvider() (provider.Provider, provider.Config, error) {
	name := a.active()
	if name == "" {
		// The common first-run mistake, so it gets a message that says what to
		// do rather than one that says what went wrong.
		return nil, provider.Config{}, errors.New(
			"no AI provider is configured; set OPENAI_API_KEY or GEMINI_API_KEY and restart")
	}
	p, ok := a.providers.Get(name)
	if !ok {
		return nil, provider.Config{}, fmt.Errorf("no provider named %q", name)
	}
	key := a.cfg.aiKeys[name]
	if key == "" {
		return nil, provider.Config{}, fmt.Errorf("%s has no API key; set %s", name, apiKeyEnvFor(name))
	}
	return p, provider.Config{
		APIKey:       key,
		Model:        a.cfg.aiModel,
		Voice:        a.cfg.aiVoice,
		Instructions: a.cfg.aiInstructions,
	}, nil
}

// authenticateDevice verifies a robot's bearer token.
func (a *app) authenticateDevice(bearer string) (string, error) {
	d, err := token.DecodeDevice(bearer, a.cfg.sessionKey, time.Now())
	if err != nil {
		// The caller logs this and answers 401; the detail stays server-side.
		return "", fmt.Errorf("%w: %v", device.ErrUnauthorized, err)
	}
	return d.DeviceID, nil
}

// deviceHandler is the robot's WebSocket endpoint.
func (a *app) deviceHandler() http.Handler {
	return &device.Handler{
		Hub:    a.hub,
		Auth:   a.authenticateDevice,
		Config: a.resolveProvider,
		Chain:  a.chain,
	}
}

// ---------------------------------------------------------------------------
// Controller API. Everything below sits behind the same GitHub sign-in as the
// website; none of it is reachable by the robot.
// ---------------------------------------------------------------------------

type providerStatus struct {
	Name         string `json:"name"`
	Configured   bool   `json:"configured"`
	DefaultModel string `json:"default_model"`
	Active       bool   `json:"active"`
}

type statusResponse struct {
	Providers []providerStatus `json:"providers"`
	Model     string           `json:"model,omitempty"`
	Voice     string           `json:"voice,omitempty"`
	Handlers  []string         `json:"handlers"`
	Devices   []device.State   `json:"devices"`
	WakeWord  string           `json:"wake_word"`

	// DeviceURL is the address to write into a robot. Not simply this page's
	// own origin: on a bench the controller is open at localhost, because Web
	// Bluetooth will not run anywhere else without HTTPS, and localhost is the
	// one address the robot certainly cannot reach.
	DeviceURL string `json:"device_url"`
	Local     bool   `json:"local"`
}

func (a *app) handleStatus(w http.ResponseWriter, r *http.Request) {
	active := a.active()
	ps := make([]providerStatus, 0, len(a.providers.All()))
	for _, p := range a.providers.All() {
		ps = append(ps, providerStatus{
			Name:         p.Name(),
			Configured:   a.cfg.aiKeys[p.Name()] != "",
			DefaultModel: p.DefaultModel(),
			Active:       p.Name() == active,
		})
	}
	writeJSON(w, http.StatusOK, statusResponse{
		Providers: ps,
		Model:     a.cfg.aiModel,
		Voice:     a.cfg.aiVoice,
		Handlers:  a.chain.Names(),
		Devices:   a.hub.Devices(),
		WakeWord:  wakeWord,
		DeviceURL: a.deviceURL(r),
		Local:     a.cfg.localMode,
	})
}

// handleSelectProvider switches the active AI service.
//
// The choice is held in memory on this instance. That is honest rather than
// ideal: making it durable would need a database, and the deployment this
// serves is a household with a robot in it. AI_PROVIDER in the environment is
// the durable setting, and the deploy script pins the service to one instance
// so that a switch made here is the switch every robot sees.
func (a *app) handleSelectProvider(w http.ResponseWriter, r *http.Request) {
	var body struct {
		Provider string `json:"provider"`
	}
	if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, 4<<10)).Decode(&body); err != nil {
		writeJSONError(w, http.StatusBadRequest, "could not read the request")
		return
	}
	if _, ok := a.providers.Get(body.Provider); !ok {
		writeJSONError(w, http.StatusBadRequest, fmt.Sprintf("no provider named %q", body.Provider))
		return
	}
	if a.cfg.aiKeys[body.Provider] == "" {
		writeJSONError(w, http.StatusBadRequest, fmt.Sprintf(
			"%s has no API key here; set %s and restart the server",
			body.Provider, apiKeyEnvFor(body.Provider)))
		return
	}

	name := body.Provider
	a.activeProvider.Store(&name)
	log.Printf("voice: active provider set to %s", name)
	a.handleStatus(w, r)
}

// handleDeviceToken issues a robot's credential.
//
// Minted here and carried to the robot over Bluetooth by the controller, so the
// token never travels over the network in the clear and never has to be typed.
func (a *app) handleDeviceToken(w http.ResponseWriter, r *http.Request) {
	var body struct {
		DeviceID string `json:"device_id"`
	}
	if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, 4<<10)).Decode(&body); err != nil {
		writeJSONError(w, http.StatusBadRequest, "could not read the request")
		return
	}
	id, err := token.CleanDeviceID(body.DeviceID)
	if err != nil {
		writeJSONError(w, http.StatusBadRequest, err.Error())
		return
	}
	token, err := token.MintDevice(id, a.cfg.sessionKey, a.cfg.deviceTokenTTL)
	if err != nil {
		writeJSONError(w, http.StatusInternalServerError, "could not issue a token")
		return
	}

	s, _ := a.currentSession(r)
	log.Printf("voice: %s issued a device token for %q", s.Login, id)

	writeJSON(w, http.StatusOK, map[string]any{
		"device_id": id,
		"token":     token,
		"expires":   time.Now().Add(a.cfg.deviceTokenTTL).UTC().Format(time.RFC3339),
	})
}

// handleDeviceAction tells a connected robot to do something.
//
// This is what the listening button in the controller drives. It goes through
// the backend rather than over Bluetooth so that it works from anywhere the
// controller does — and because on a board with no wake word, this is the only
// way to start a conversation at all.
//
// The same path the intent harness will use when a model asks for a movement.
func (a *app) handleDeviceAction(w http.ResponseWriter, r *http.Request) {
	var body struct {
		DeviceID string `json:"device_id"`
		Cmd      string `json:"cmd"`
		Gesture  string `json:"gesture"`
	}
	if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, 4<<10)).Decode(&body); err != nil {
		writeJSONError(w, http.StatusBadRequest, "could not read the request")
		return
	}
	id, err := token.CleanDeviceID(body.DeviceID)
	if err != nil {
		writeJSONError(w, http.StatusBadRequest, err.Error())
		return
	}
	if body.Cmd == "" && body.Gesture == "" {
		writeJSONError(w, http.StatusBadRequest, "give a cmd or a gesture")
		return
	}
	// The robot's command parser reads a short line. Anything longer is a
	// mistake or an attempt at one, and is cheaper to refuse here than to
	// truncate on a microcontroller.
	if len(body.Cmd) > 32 || len(body.Gesture) > 32 {
		writeJSONError(w, http.StatusBadRequest, "cmd and gesture must be short")
		return
	}

	if err := a.hub.Send(id, protocol.Message{
		Type:    protocol.MsgAct,
		Cmd:     body.Cmd,
		Gesture: body.Gesture,
	}); err != nil {
		// Not connected is the ordinary case — the robot is off, or its Wi-Fi
		// dropped — so it reads as a bad request rather than a server fault.
		writeJSONError(w, http.StatusConflict, err.Error())
		return
	}
	writeJSON(w, http.StatusOK, map[string]string{"device_id": id, "status": "sent"})
}

// handleControllerSocket streams live updates to the web controller.
func (a *app) handleControllerSocket(w http.ResponseWriter, r *http.Request) {
	ws, err := websocket.Accept(w, r, &websocket.AcceptOptions{
		// Same origin only. This is a browser endpoint, so unlike the robot's
		// it has an Origin header worth checking.
		OriginPatterns: []string{r.Host},
	})
	if err != nil {
		return
	}
	defer ws.Close(websocket.StatusNormalClosure, "")

	updates, unsubscribe := a.hub.Subscribe()
	defer unsubscribe()

	ctx := r.Context()
	for {
		select {
		case <-ctx.Done():
			return
		case u, ok := <-updates:
			if !ok {
				return
			}
			b, err := json.Marshal(u)
			if err != nil {
				continue
			}
			writeCtx, cancel := context.WithTimeout(ctx, 10*time.Second)
			err = ws.Write(writeCtx, websocket.MessageText, b)
			cancel()
			if err != nil {
				return
			}
		}
	}
}

// apiKeyEnvFor names the environment variable a provider's key comes from, so
// that an error message tells someone what to actually do.
func apiKeyEnvFor(provider string) string {
	switch provider {
	case "openai":
		return "OPENAI_API_KEY"
	case "gemini":
		return "GEMINI_API_KEY"
	default:
		return "the provider's API key variable"
	}
}

func writeJSON(w http.ResponseWriter, status int, v any) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.Header().Set("Cache-Control", "no-store")
	w.WriteHeader(status)
	if err := json.NewEncoder(w).Encode(v); err != nil {
		log.Printf("api: writing response: %v", err)
	}
}

func writeJSONError(w http.ResponseWriter, status int, message string) {
	writeJSON(w, status, map[string]string{"error": message})
}

// serveControllerHandler serves the web controller's files.
//
// Marked private for the same reason the website is: the pages behind this
// sign-in must not sit in a shared cache where the next visitor can be handed
// one without earning it.
func (a *app) serveControllerHandler() http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Cache-Control", "private, no-cache")
		a.controller.ServeHTTP(w, r)
	})
}
