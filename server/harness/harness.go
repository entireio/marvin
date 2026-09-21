// Package harness decides what happens to everything the AI service says.
//
// Today it does one thing: pass the reply through to the robot. That is the
// whole of the assistant behaviour in the brief — you ask about the weather in
// San Francisco and Marvin answers — and it deliberately is not dressed up as
// more than it is.
//
// What the package buys is the shape for what comes next. Detecting that
// someone asked for a note to be taken, or told the robot to turn around, is
// then another Handler in the chain rather than a change to the audio path. The
// detection itself is the model's own function calling: a handler declares the
// tools it can service, the model calls one when the conversation warrants it,
// and the handler acts. No second classifier, no keyword matching, and the
// model keeps the context that makes "actually, make that tomorrow" work.
package harness

import (
	"context"
	"time"

	"github.com/spedemon/marvin/server/protocol"
	"github.com/spedemon/marvin/server/provider"
)

// Observation is something worth showing a human in the web controller. It
// never reaches the robot.
type Observation struct {
	Kind string    `json:"kind"` // "transcript", "tool", "error", "state"
	Role string    `json:"role,omitempty"`
	Text string    `json:"text,omitempty"`
	At   time.Time `json:"at"`
}

// Emitter is everything a handler is allowed to do. Handlers get no direct
// access to the connection or the provider session, which is what keeps them
// composable and testable.
type Emitter interface {
	// Audio queues synthesized speech for the robot. PCM16 mono at the
	// device's rate; framing is not the handler's problem.
	Audio(pcm []byte) error

	// Control sends a control message to the robot.
	Control(m protocol.Message) error

	// Observe publishes to the web controller.
	Observe(o Observation)

	// ToolResult answers a tool call, which lets the model carry on.
	ToolResult(id string, result any) error
}

// Handler reacts to one event from the AI service.
//
// Handlers run in order and all of them see every event. A handler that returns
// an error stops the chain for that event, on the principle that a handler
// which could not do its job should not have later ones acting as though it
// had.
type Handler interface {
	Name() string
	OnEvent(ctx context.Context, ev provider.Event, out Emitter) error
}

// ToolDeclarer is the optional half of Handler. A handler that wants the model
// to be able to call it declares the tools here, and the chain collects them
// into the session configuration at dial time.
//
// Nothing implements this yet. It is here because the alternative — discovering
// at the point of adding the first tool that tools have to be plumbed through
// dialling, configuration and two provider clients — is how a clean seam turns
// into a refactor.
type ToolDeclarer interface {
	Tools() []provider.Tool
}

// Chain runs handlers in order.
type Chain []Handler

// OnEvent passes ev to each handler in turn.
func (c Chain) OnEvent(ctx context.Context, ev provider.Event, out Emitter) error {
	for _, h := range c {
		if err := h.OnEvent(ctx, ev, out); err != nil {
			return err
		}
	}
	return nil
}

// Tools gathers every tool the chain declares, for the provider session.
func (c Chain) Tools() []provider.Tool {
	var tools []provider.Tool
	for _, h := range c {
		if td, ok := h.(ToolDeclarer); ok {
			tools = append(tools, td.Tools()...)
		}
	}
	return tools
}

// Names lists the handlers, for logging and the status endpoint.
func (c Chain) Names() []string {
	out := make([]string, 0, len(c))
	for _, h := range c {
		out = append(out, h.Name())
	}
	return out
}
