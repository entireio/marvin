// Package provider bridges a Marvin conversation to a realtime AI service.
//
// The interfaces here are the whole of the portability story. Everything above
// them — the device link, the harness, the web UI — deals in 16 kHz mono PCM16
// and a small set of events, and has no idea whether OpenAI or Gemini is on the
// other side. Adding a third service means adding a file to this package.
//
// Each implementation owns its own resampling, so callers never have to think
// about the fact that OpenAI wants 24 kHz both ways while Gemini wants 16 kHz
// in and 24 kHz out.
package provider

import (
	"context"
	"encoding/json"
	"errors"
)

// EventKind discriminates the events a session emits.
type EventKind string

const (
	// EventTurnStart marks the model beginning a reply.
	EventTurnStart EventKind = "turn_start"
	// EventAudio carries synthesized speech: PCM16 mono at the device's rate,
	// resampled by the provider so the rest of the server never has to care.
	EventAudio EventKind = "audio"
	// EventTranscript carries text for either speaker. Useful for the web UI
	// and, later, for intent detection that wants words rather than audio.
	EventTranscript EventKind = "transcript"
	// EventTurnEnd marks the reply complete.
	EventTurnEnd EventKind = "turn_end"
	// EventInterrupted says the model stopped because the user spoke over it.
	// Nothing produces this yet — the robot mutes its microphone while it
	// speaks, for want of echo cancellation — but both services report it and
	// dropping the information here would be harder to add back later.
	EventInterrupted EventKind = "interrupted"
	// EventToolCall is the model asking for something to be done. This is the
	// seam the intent harness is built on: "take a note", "turn around".
	EventToolCall EventKind = "tool_call"
	// EventError is a failure the session could not recover from on its own.
	EventError EventKind = "error"
)

// Role says who spoke a transcript.
type Role string

const (
	RoleUser      Role = "user"
	RoleAssistant Role = "assistant"
)

// ToolCall is a request from the model to run one of the declared tools.
type ToolCall struct {
	ID   string          `json:"id"`
	Name string          `json:"name"`
	Args json.RawMessage `json:"args"`
}

// Event is one thing that happened in a session.
type Event struct {
	Kind  EventKind
	Audio []byte // EventAudio: PCM16 mono at protocol.DeviceSampleRate
	Text  string // EventTranscript
	Role  Role   // EventTranscript
	Tool  *ToolCall
	Err   error // EventError
}

// Tool declares a capability to the model.
//
// Intents are detected by the model's own function calling rather than by a
// separate classifier behind it. That is both simpler and better: the model
// already has the conversation in front of it, so it knows that "actually,
// make that tomorrow" amends the previous request.
type Tool struct {
	Name        string          `json:"name"`
	Description string          `json:"description"`
	Parameters  json.RawMessage `json:"parameters"` // JSON Schema
}

// Config is what a session needs to start.
type Config struct {
	APIKey       string
	Model        string // empty means the provider's default
	Voice        string // empty means the provider's default
	Instructions string // the system prompt
	Tools        []Tool
}

// Session is one live conversation.
//
// A session is driven from a single goroutine: call SendAudio and CommitTurn
// from one place and read Events from another. Implementations may buffer, but
// none of them are safe against concurrent senders.
type Session interface {
	// SendAudio streams microphone audio. PCM16 mono at the device's rate; the
	// implementation resamples.
	SendAudio(pcm []byte) error

	// CommitTurn says the user has stopped speaking and a reply is wanted.
	// Redundant when the service's own turn detection is doing the work, which
	// is the normal case, but a device that decides for itself needs a way to
	// say so.
	CommitTurn() error

	// SendToolResult returns the outcome of a ToolCall.
	SendToolResult(id string, result any) error

	// Events is closed when the session ends. A reader must drain it, or the
	// session's internal goroutine will block.
	Events() <-chan Event

	// Close ends the session. Safe to call more than once.
	Close() error
}

// Provider dials sessions for one service.
type Provider interface {
	// Name is the stable identifier used in configuration and the web UI.
	Name() string

	// DefaultModel is used when Config.Model is empty.
	DefaultModel() string

	// Dial opens a session. The context bounds the connection attempt, not the
	// life of the session.
	Dial(ctx context.Context, cfg Config) (Session, error)
}

// ErrNoAPIKey is returned by Dial when the provider has no credentials. The web
// UI turns this into "not configured" rather than a failure, because a
// deployment that only has one provider's key is the normal case.
var ErrNoAPIKey = errors.New("provider: no API key configured")

// Set is the providers a deployment has, in a stable order.
//
// Keys live in the environment rather than in a database: the server is
// stateless and runs several instances at once, so there is nowhere to keep a
// secret that all of them would agree on, and the cloud secret managers already
// solve this problem properly.
type Set struct {
	providers []Provider
	byName    map[string]Provider
}

// NewSet builds a registry. Later duplicates of a name are ignored.
func NewSet(ps ...Provider) *Set {
	s := &Set{byName: make(map[string]Provider, len(ps))}
	for _, p := range ps {
		if _, seen := s.byName[p.Name()]; seen {
			continue
		}
		s.byName[p.Name()] = p
		s.providers = append(s.providers, p)
	}
	return s
}

// Get returns the named provider.
func (s *Set) Get(name string) (Provider, bool) {
	p, ok := s.byName[name]
	return p, ok
}

// All returns every registered provider, in registration order.
func (s *Set) All() []Provider { return s.providers }

// Names returns every registered provider's name, in registration order.
func (s *Set) Names() []string {
	out := make([]string, 0, len(s.providers))
	for _, p := range s.providers {
		out = append(out, p.Name())
	}
	return out
}
