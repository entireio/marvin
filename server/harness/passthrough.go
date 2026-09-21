package harness

import (
	"context"
	"time"

	"github.com/spedemon/marvin/server/protocol"
	"github.com/spedemon/marvin/server/provider"
)

// PassThrough turns an AI reply into sound coming out of the robot, and the
// conversation into something a person can read in the web controller.
//
// It also owns the turn choreography — telling the robot when to stop listening
// and when it may start again. That belongs in a handler rather than in the
// connection code because it is a decision about behaviour: a later handler
// might want to nod before answering, or hold the reply until the robot has
// finished moving.
type PassThrough struct{}

func (PassThrough) Name() string { return "passthrough" }

func (PassThrough) OnEvent(ctx context.Context, ev provider.Event, out Emitter) error {
	switch ev.Kind {
	case provider.EventTurnStart:
		// The robot stops sending microphone audio here. Its microphone and
		// speaker are centimetres apart with no echo cancellation between
		// them, so an open uplink would feed Marvin its own voice and the
		// service's turn detection would treat that as an interruption.
		return out.Control(protocol.Message{Type: protocol.MsgSpeakBegin})

	case provider.EventAudio:
		return out.Audio(ev.Audio)

	case provider.EventTurnEnd, provider.EventInterrupted:
		return out.Control(protocol.Message{Type: protocol.MsgSpeakEnd})

	case provider.EventTranscript:
		out.Observe(Observation{
			Kind: "transcript",
			Role: string(ev.Role),
			Text: ev.Text,
			At:   time.Now(),
		})

	case provider.EventToolCall:
		// No handler claims tools yet, and a model left waiting on a call that
		// never returns simply stops talking. Answering plainly keeps the
		// conversation alive and puts the reason in front of whoever is
		// watching the controller.
		out.Observe(Observation{
			Kind: "tool",
			Text: "unhandled tool call: " + ev.Tool.Name,
			At:   time.Now(),
		})
		return out.ToolResult(ev.Tool.ID, map[string]any{
			"error": "this robot has no handler for " + ev.Tool.Name,
		})

	case provider.EventError:
		out.Observe(Observation{Kind: "error", Text: ev.Err.Error(), At: time.Now()})
		// Let the robot stop waiting; the session above decides whether this is
		// fatal.
		return out.Control(protocol.Message{
			Type:    protocol.MsgError,
			Message: ev.Err.Error(),
		})
	}
	return nil
}
