package provider

import (
	"context"
	"encoding/base64"
	"encoding/json"
	"fmt"
	"log"
	"net/url"
	"strings"
	"sync"

	"github.com/coder/websocket"

	"github.com/spedemon/marvin/server/audio"
	"github.com/spedemon/marvin/server/protocol"
)

// Google's Gemini Live API. Same shape as the OpenAI client and deliberately
// so: the differences between the two services are confined to this file and
// openai.go, and everything above sees one interface.
//
// Gemini is the easier of the two on the uplink — it takes 16 kHz natively, so
// microphone audio passes through untouched — and the same on the downlink,
// where its 24 kHz output has to come back down to the robot's rate.

const (
	geminiLiveHost = "generativelanguage.googleapis.com"
	geminiLivePath = "/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent"

	geminiInputRate  = 16000 // matches the device exactly
	geminiOutputRate = 24000

	geminiDefaultModel = "gemini-3.1-flash-live-preview"
	geminiDefaultVoice = "Kore"
)

type geminiProvider struct{}

// NewGemini returns the Gemini Live provider.
func NewGemini() Provider { return geminiProvider{} }

func (geminiProvider) Name() string         { return "gemini" }
func (geminiProvider) DefaultModel() string { return geminiDefaultModel }

func (p geminiProvider) Dial(ctx context.Context, cfg Config) (Session, error) {
	if cfg.APIKey == "" {
		return nil, ErrNoAPIKey
	}
	model := cfg.Model
	if model == "" {
		model = geminiDefaultModel
	}

	// The key goes in the query string because that is the only authentication
	// this endpoint accepts. It never reaches a log: errors below are reported
	// without the URL, and the URL is not stored on the session.
	u := url.URL{
		Scheme:   "wss",
		Host:     geminiLiveHost,
		Path:     geminiLivePath,
		RawQuery: url.Values{"key": {cfg.APIKey}}.Encode(),
	}

	conn, _, err := websocket.Dial(ctx, u.String(), nil)
	if err != nil {
		return nil, fmt.Errorf("gemini: dial %s: %w", geminiLiveHost, err)
	}
	conn.SetReadLimit(4 << 20)

	down, err := audio.NewConverter(geminiOutputRate, protocol.DeviceSampleRate)
	if err != nil {
		conn.Close(websocket.StatusInternalError, "resampler")
		return nil, err
	}

	sessCtx, cancel := context.WithCancel(context.Background())
	s := &geminiSession{
		conn:   conn,
		ctx:    sessCtx,
		cancel: cancel,
		down:   down,
		events: make(chan Event, 64),
	}

	if err := s.setup(ctx, cfg, model); err != nil {
		s.Close()
		return nil, err
	}
	go s.readLoop()
	return s, nil
}

type geminiSession struct {
	conn   *websocket.Conn
	ctx    context.Context
	cancel context.CancelFunc

	writeMu sync.Mutex
	down    *audio.Converter
	events  chan Event

	closeOnce sync.Once
	turnOpen  bool // whether a reply is currently streaming
}

// setup sends the one configuration message the API requires before anything
// else. The service answers with setupComplete; audio sent before that is
// discarded, so the read loop does not start until this returns.
func (s *geminiSession) setup(ctx context.Context, cfg Config, model string) error {
	setup := map[string]any{
		"model": "models/" + model,
		"generationConfig": map[string]any{
			"responseModalities": []string{"AUDIO"},
			"speechConfig": map[string]any{
				"voiceConfig": map[string]any{
					"prebuiltVoiceConfig": map[string]any{
						"voiceName": orDefault(cfg.Voice, geminiDefaultVoice),
					},
				},
			},
		},
		// Both transcriptions on: the web UI shows them, and the intent harness
		// will want words rather than audio. Neither costs extra latency,
		// because they arrive alongside the audio rather than gating it.
		"inputAudioTranscription":  map[string]any{},
		"outputAudioTranscription": map[string]any{},
	}
	if cfg.Instructions != "" {
		setup["systemInstruction"] = map[string]any{
			"parts": []map[string]any{{"text": cfg.Instructions}},
		}
	}
	if len(cfg.Tools) > 0 {
		decls := make([]map[string]any, 0, len(cfg.Tools))
		for _, t := range cfg.Tools {
			decls = append(decls, map[string]any{
				"name":        t.Name,
				"description": t.Description,
				"parameters":  t.Parameters,
			})
		}
		setup["tools"] = []map[string]any{{"functionDeclarations": decls}}
	}

	return s.send(ctx, map[string]any{"setup": setup})
}

func (s *geminiSession) send(ctx context.Context, v any) error {
	b, err := json.Marshal(v)
	if err != nil {
		return err
	}
	s.writeMu.Lock()
	defer s.writeMu.Unlock()
	return s.conn.Write(ctx, websocket.MessageText, b)
}

func (s *geminiSession) SendAudio(pcm []byte) error {
	if len(pcm) == 0 {
		return nil
	}
	// No conversion: the device and this API agree on 16 kHz, which is the one
	// happy accident in the whole audio path.
	return s.send(s.ctx, map[string]any{
		"realtimeInput": map[string]any{
			"audio": map[string]any{
				"data":     base64.StdEncoding.EncodeToString(pcm),
				"mimeType": fmt.Sprintf("audio/pcm;rate=%d", geminiInputRate),
			},
		},
	})
}

func (s *geminiSession) CommitTurn() error {
	// Gemini's own voice activity detection normally closes the turn. This says
	// the audio stream has ended, which makes it commit immediately rather than
	// waiting out its silence window.
	return s.send(s.ctx, map[string]any{
		"realtimeInput": map[string]any{"audioStreamEnd": true},
	})
}

func (s *geminiSession) SendToolResult(id string, result any) error {
	payload, err := json.Marshal(result)
	if err != nil {
		return err
	}
	var response map[string]any
	if err := json.Unmarshal(payload, &response); err != nil {
		// A tool that returns something other than an object still has to be
		// reported as one: the API's functionResponse takes a struct.
		response = map[string]any{"result": json.RawMessage(payload)}
	}
	return s.send(s.ctx, map[string]any{
		"toolResponse": map[string]any{
			"functionResponses": []map[string]any{{"id": id, "response": response}},
		},
	})
}

func (s *geminiSession) Events() <-chan Event { return s.events }

func (s *geminiSession) Close() error {
	s.closeOnce.Do(func() {
		s.cancel()
		s.conn.Close(websocket.StatusNormalClosure, "")
	})
	return nil
}

// geminiMessage is the subset of the server message we act on.
type geminiMessage struct {
	SetupComplete *struct{} `json:"setupComplete"`
	ServerContent *struct {
		ModelTurn *struct {
			Parts []struct {
				InlineData *struct {
					MimeType string `json:"mimeType"`
					Data     string `json:"data"`
				} `json:"inlineData"`
				Text string `json:"text"`
			} `json:"parts"`
		} `json:"modelTurn"`
		InputTranscription  *struct{ Text string } `json:"inputTranscription"`
		OutputTranscription *struct{ Text string } `json:"outputTranscription"`
		TurnComplete        bool                   `json:"turnComplete"`
		Interrupted         bool                   `json:"interrupted"`
	} `json:"serverContent"`
	ToolCall *struct {
		FunctionCalls []struct {
			ID   string          `json:"id"`
			Name string          `json:"name"`
			Args json.RawMessage `json:"args"`
		} `json:"functionCalls"`
	} `json:"toolCall"`
	Error *struct {
		Message string `json:"message"`
	} `json:"error"`
}

func (s *geminiSession) readLoop() {
	defer close(s.events)
	defer s.Close()

	for {
		_, data, err := s.conn.Read(s.ctx)
		if err != nil {
			if s.ctx.Err() == nil {
				s.emit(Event{Kind: EventError, Err: fmt.Errorf("gemini: read: %w", err)})
			}
			return
		}

		// Unlike OpenAI, this API sends its JSON as binary frames as often as
		// text ones, so the frame type is not a useful filter.
		var msg geminiMessage
		if err := json.Unmarshal(data, &msg); err != nil {
			log.Printf("gemini: undecodable message: %v", err)
			continue
		}

		if msg.Error != nil {
			s.emit(Event{Kind: EventError, Err: fmt.Errorf("gemini: %s", msg.Error.Message)})
			continue
		}
		if msg.ToolCall != nil {
			for _, c := range msg.ToolCall.FunctionCalls {
				args := c.Args
				if len(args) == 0 {
					args = json.RawMessage("{}")
				}
				s.emit(Event{Kind: EventToolCall, Tool: &ToolCall{ID: c.ID, Name: c.Name, Args: args}})
			}
			continue
		}

		sc := msg.ServerContent
		if sc == nil {
			continue // setupComplete and other lifecycle messages
		}

		if sc.Interrupted {
			s.down.Reset()
			s.turnOpen = false
			s.emit(Event{Kind: EventInterrupted})
		}
		if sc.InputTranscription != nil && sc.InputTranscription.Text != "" {
			s.emit(Event{Kind: EventTranscript, Role: RoleUser, Text: sc.InputTranscription.Text})
		}
		if sc.OutputTranscription != nil && sc.OutputTranscription.Text != "" {
			s.emit(Event{Kind: EventTranscript, Role: RoleAssistant, Text: sc.OutputTranscription.Text})
		}

		if sc.ModelTurn != nil {
			for _, part := range sc.ModelTurn.Parts {
				if part.InlineData == nil || !strings.HasPrefix(part.InlineData.MimeType, "audio/") {
					continue
				}
				pcm, err := base64.StdEncoding.DecodeString(part.InlineData.Data)
				if err != nil {
					log.Printf("gemini: bad audio part: %v", err)
					continue
				}
				// There is no explicit "reply starting" message, so the first
				// audio of a turn is what marks the start.
				if !s.turnOpen {
					s.turnOpen = true
					s.emit(Event{Kind: EventTurnStart})
				}
				s.emit(Event{Kind: EventAudio, Audio: append([]byte(nil), s.down.Convert(pcm)...)})
			}
		}

		if sc.TurnComplete {
			s.down.Reset()
			s.turnOpen = false
			s.emit(Event{Kind: EventTurnEnd})
		}
	}
}

func (s *geminiSession) emit(e Event) {
	select {
	case s.events <- e:
	case <-s.ctx.Done():
	}
}

func orDefault(v, fallback string) string {
	if v == "" {
		return fallback
	}
	return v
}
