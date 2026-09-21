package provider

import (
	"context"
	"encoding/base64"
	"encoding/json"
	"fmt"
	"log"
	"net/http"
	"sync"

	"github.com/coder/websocket"

	"github.com/spedemon/marvin/server/audio"
	"github.com/spedemon/marvin/server/protocol"
)

// OpenAI's Realtime API. Raw WebSocket rather than the SDK: the whole protocol
// is JSON events over one socket, and pulling in a client library to send them
// would be more code than sending them.
//
// Everything here runs at 24 kHz, which is the service's native rate, and the
// conversion to and from the robot's 16 kHz happens at the edges of this file.

const (
	openAIRealtimeURL = "wss://api.openai.com/v1/realtime"
	openAISampleRate  = 24000
	// openAIDefaultModel is overridable through configuration precisely because
	// it will go stale.
	openAIDefaultModel = "gpt-realtime-2.1"
	openAIDefaultVoice = "marin"
)

type openAIProvider struct{}

// NewOpenAI returns the OpenAI Realtime provider.
func NewOpenAI() Provider { return openAIProvider{} }

func (openAIProvider) Name() string         { return "openai" }
func (openAIProvider) DefaultModel() string { return openAIDefaultModel }

func (p openAIProvider) Dial(ctx context.Context, cfg Config) (Session, error) {
	if cfg.APIKey == "" {
		return nil, ErrNoAPIKey
	}
	model := cfg.Model
	if model == "" {
		model = openAIDefaultModel
	}

	conn, _, err := websocket.Dial(ctx, openAIRealtimeURL+"?model="+model, &websocket.DialOptions{
		HTTPHeader: http.Header{"Authorization": {"Bearer " + cfg.APIKey}},
	})
	if err != nil {
		return nil, fmt.Errorf("openai: dial: %w", err)
	}
	// Audio deltas are a few kilobytes; this is headroom, not a target. Without
	// a limit a confused or hostile peer could make us allocate without bound.
	conn.SetReadLimit(4 << 20)

	up, err := audio.NewConverter(protocol.DeviceSampleRate, openAISampleRate)
	if err != nil {
		conn.Close(websocket.StatusInternalError, "resampler")
		return nil, err
	}
	down, err := audio.NewConverter(openAISampleRate, protocol.DeviceSampleRate)
	if err != nil {
		conn.Close(websocket.StatusInternalError, "resampler")
		return nil, err
	}

	sessCtx, cancel := context.WithCancel(context.Background())
	s := &openAISession{
		conn:   conn,
		cancel: cancel,
		ctx:    sessCtx,
		up:     up,
		down:   down,
		events: make(chan Event, 64),
	}

	if err := s.configure(ctx, cfg); err != nil {
		s.Close()
		return nil, err
	}
	go s.readLoop()
	return s, nil
}

type openAISession struct {
	conn   *websocket.Conn
	ctx    context.Context
	cancel context.CancelFunc

	writeMu sync.Mutex // one writer at a time: the library requires it
	up      *audio.Converter
	down    *audio.Converter
	events  chan Event

	closeOnce sync.Once
}

// configure sends the one session.update that sets up the whole conversation.
//
// The nesting below is the shape the Realtime API settled on at GA — audio
// input and output configured separately, each with its own format — and is
// the part most likely to need revisiting when the API moves. Keeping it in one
// function means that revisit is one edit.
func (s *openAISession) configure(ctx context.Context, cfg Config) error {
	voice := cfg.Voice
	if voice == "" {
		voice = openAIDefaultVoice
	}

	type format struct {
		Type string `json:"type"`
		Rate int    `json:"rate,omitempty"`
	}
	pcm := format{Type: "audio/pcm", Rate: openAISampleRate}

	session := map[string]any{
		"audio": map[string]any{
			"input": map[string]any{
				"format": pcm,
				// Let the service decide when the user has finished speaking.
				// It is listening to the same audio we are and is better at it
				// than an energy threshold on a microcontroller.
				"turn_detection": map[string]any{"type": "server_vad"},
			},
			"output": map[string]any{
				"format": pcm,
				"voice":  voice,
			},
		},
	}
	if cfg.Instructions != "" {
		session["instructions"] = cfg.Instructions
	}
	if len(cfg.Tools) > 0 {
		tools := make([]map[string]any, 0, len(cfg.Tools))
		for _, t := range cfg.Tools {
			tools = append(tools, map[string]any{
				"type":        "function",
				"name":        t.Name,
				"description": t.Description,
				"parameters":  t.Parameters,
			})
		}
		session["tools"] = tools
	}

	return s.send(ctx, map[string]any{"type": "session.update", "session": session})
}

func (s *openAISession) send(ctx context.Context, v any) error {
	b, err := json.Marshal(v)
	if err != nil {
		return err
	}
	s.writeMu.Lock()
	defer s.writeMu.Unlock()
	return s.conn.Write(ctx, websocket.MessageText, b)
}

func (s *openAISession) SendAudio(pcm []byte) error {
	resampled := s.up.Convert(pcm)
	if len(resampled) == 0 {
		return nil
	}
	return s.send(s.ctx, map[string]any{
		"type":  "input_audio_buffer.append",
		"audio": base64.StdEncoding.EncodeToString(resampled),
	})
}

func (s *openAISession) CommitTurn() error {
	if err := s.send(s.ctx, map[string]any{"type": "input_audio_buffer.commit"}); err != nil {
		return err
	}
	return s.send(s.ctx, map[string]any{"type": "response.create"})
}

func (s *openAISession) SendToolResult(id string, result any) error {
	payload, err := json.Marshal(result)
	if err != nil {
		return err
	}
	if err := s.send(s.ctx, map[string]any{
		"type": "conversation.item.create",
		"item": map[string]any{
			"type":    "function_call_output",
			"call_id": id,
			"output":  string(payload),
		},
	}); err != nil {
		return err
	}
	// The model does not resume on its own after a tool result.
	return s.send(s.ctx, map[string]any{"type": "response.create"})
}

func (s *openAISession) Events() <-chan Event { return s.events }

func (s *openAISession) Close() error {
	s.closeOnce.Do(func() {
		s.cancel()
		s.conn.Close(websocket.StatusNormalClosure, "")
	})
	return nil
}

// openAIEvent is the union of the server event fields we act on. Unmarshalling
// into one struct rather than switching on type and re-parsing keeps the read
// loop to a single pass.
type openAIEvent struct {
	Type       string `json:"type"`
	Delta      string `json:"delta"`
	Transcript string `json:"transcript"`
	CallID     string `json:"call_id"`
	Name       string `json:"name"`
	Arguments  string `json:"arguments"`
	Error      *struct {
		Message string `json:"message"`
		Code    string `json:"code"`
	} `json:"error"`
}

func (s *openAISession) readLoop() {
	defer close(s.events)
	defer s.Close()

	for {
		typ, data, err := s.conn.Read(s.ctx)
		if err != nil {
			if s.ctx.Err() == nil {
				s.emit(Event{Kind: EventError, Err: fmt.Errorf("openai: read: %w", err)})
			}
			return
		}
		if typ != websocket.MessageText {
			continue // the service sends JSON only
		}

		var ev openAIEvent
		if err := json.Unmarshal(data, &ev); err != nil {
			log.Printf("openai: undecodable event: %v", err)
			continue
		}

		switch ev.Type {
		case "response.created":
			s.emit(Event{Kind: EventTurnStart})

		case "response.output_audio.delta":
			pcm, err := base64.StdEncoding.DecodeString(ev.Delta)
			if err != nil {
				log.Printf("openai: bad audio delta: %v", err)
				continue
			}
			// Copy: Convert reuses its output buffer, and this leaves our
			// goroutine.
			out := append([]byte(nil), s.down.Convert(pcm)...)
			s.emit(Event{Kind: EventAudio, Audio: out})

		case "response.output_audio_transcript.delta":
			s.emit(Event{Kind: EventTranscript, Role: RoleAssistant, Text: ev.Delta})

		case "conversation.item.input_audio_transcription.completed":
			s.emit(Event{Kind: EventTranscript, Role: RoleUser, Text: ev.Transcript})

		case "response.function_call_arguments.done":
			args := json.RawMessage(ev.Arguments)
			if len(args) == 0 {
				args = json.RawMessage("{}")
			}
			s.emit(Event{Kind: EventToolCall, Tool: &ToolCall{
				ID: ev.CallID, Name: ev.Name, Args: args,
			}})

		case "response.done":
			// Reset the resamplers between turns. Their filter history is
			// meaningless across a silence, and carrying it in smears the end
			// of one reply into the start of the next.
			s.down.Reset()
			s.emit(Event{Kind: EventTurnEnd})

		case "error":
			msg := "unspecified error"
			if ev.Error != nil {
				msg = ev.Error.Message
			}
			s.emit(Event{Kind: EventError, Err: fmt.Errorf("openai: %s", msg)})

		default:
			// The service emits a great many lifecycle events. Ignoring the
			// ones we do not act on is deliberate, not an oversight.
		}
	}
}

// emit delivers an event unless the session is closing. Without the select a
// consumer that has gone away would wedge this goroutine for good.
func (s *openAISession) emit(e Event) {
	select {
	case s.events <- e:
	case <-s.ctx.Done():
	}
}
