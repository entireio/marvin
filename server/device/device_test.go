package device

import (
	"context"
	"errors"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/coder/websocket"

	"github.com/spedemon/marvin/server/harness"
	"github.com/spedemon/marvin/server/protocol"
	"github.com/spedemon/marvin/server/provider"
)

// A stub AI service, so the whole device path can be tested without a network,
// an API key, or a robot.

type stubProvider struct {
	mu      sync.Mutex
	session *stubSession
	dialErr error
}

func (p *stubProvider) Name() string         { return "stub" }
func (p *stubProvider) DefaultModel() string { return "stub-1" }

func (p *stubProvider) Dial(ctx context.Context, cfg provider.Config) (provider.Session, error) {
	p.mu.Lock()
	defer p.mu.Unlock()
	if p.dialErr != nil {
		return nil, p.dialErr
	}
	p.session = &stubSession{events: make(chan provider.Event, 16)}
	return p.session, nil
}

func (p *stubProvider) current() *stubSession {
	p.mu.Lock()
	defer p.mu.Unlock()
	return p.session
}

type stubSession struct {
	events chan provider.Event

	mu        sync.Mutex
	uplink    []byte
	committed bool
	closed    bool
}

func (s *stubSession) SendAudio(pcm []byte) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.uplink = append(s.uplink, pcm...)
	return nil
}

func (s *stubSession) CommitTurn() error {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.committed = true
	return nil
}

func (s *stubSession) SendToolResult(string, any) error { return nil }
func (s *stubSession) Events() <-chan provider.Event    { return s.events }

func (s *stubSession) Close() error {
	s.mu.Lock()
	defer s.mu.Unlock()
	if !s.closed {
		s.closed = true
		close(s.events)
	}
	return nil
}

func (s *stubSession) uplinkBytes() int {
	s.mu.Lock()
	defer s.mu.Unlock()
	return len(s.uplink)
}

// testRig starts a server and returns a connected client socket.
func testRig(t *testing.T, p provider.Provider) (*websocket.Conn, *Hub, context.Context) {
	t.Helper()

	hub := NewHub()
	h := &Handler{
		Hub:  hub,
		Auth: func(token string) (string, error) { return "marvin-test", nil },
		Config: func() (provider.Provider, provider.Config, error) {
			return p, provider.Config{APIKey: "x"}, nil
		},
		Chain: harness.Chain{harness.PassThrough{}},
	}
	srv := httptest.NewServer(h)
	t.Cleanup(srv.Close)

	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	t.Cleanup(cancel)

	ws, _, err := websocket.Dial(ctx, "ws"+strings.TrimPrefix(srv.URL, "http"), &websocket.DialOptions{
		HTTPHeader: http.Header{"Authorization": {"Bearer test-token"}},
	})
	if err != nil {
		t.Fatalf("dial: %v", err)
	}
	t.Cleanup(func() { ws.Close(websocket.StatusNormalClosure, "") })
	return ws, hub, ctx
}

func sendMsg(t *testing.T, ws *websocket.Conn, ctx context.Context, m protocol.Message) {
	t.Helper()
	b, err := protocol.EncodeMessage(m)
	if err != nil {
		t.Fatal(err)
	}
	if err := ws.Write(ctx, websocket.MessageText, b); err != nil {
		t.Fatalf("write %s: %v", m.Type, err)
	}
}

// expectControl reads until a control message arrives, collecting any audio
// frames it passes on the way.
func expectControl(t *testing.T, ws *websocket.Conn, ctx context.Context, want protocol.MsgType) (protocol.Message, []byte) {
	t.Helper()
	var audio []byte
	for {
		typ, data, err := ws.Read(ctx)
		if err != nil {
			t.Fatalf("waiting for %s: %v", want, err)
		}
		if typ == websocket.MessageBinary {
			f, err := protocol.Decode(data)
			if err != nil {
				t.Fatalf("bad frame while waiting for %s: %v", want, err)
			}
			if f.Type != protocol.FrameAudioDown {
				t.Fatalf("got frame type %s on the downlink", f.Type)
			}
			audio = append(audio, f.Payload...)
			continue
		}
		msg, err := protocol.DecodeMessage(data)
		if err != nil {
			t.Fatalf("undecodable control message: %v", err)
		}
		if msg.Type != want {
			t.Fatalf("got control message %q, want %q", msg.Type, want)
		}
		return msg, audio
	}
}

// The whole point of the backend, start to finish: the robot says it heard the
// wake word, streams what was said, and gets speech back.
func TestConversationRoundTrip(t *testing.T) {
	p := &stubProvider{}
	ws, hub, ctx := testRig(t, p)

	sendMsg(t, ws, ctx, protocol.Message{
		Type:       protocol.MsgHello,
		DeviceID:   "marvin-test",
		Firmware:   "test",
		Codec:      protocol.CodecPCM16,
		SampleRate: protocol.DeviceSampleRate,
	})
	if _, _, err := readUntilReady(ws, ctx); err != nil {
		t.Fatal(err)
	}

	sendMsg(t, ws, ctx, protocol.Message{Type: protocol.MsgWake})
	listen, _ := expectControl(t, ws, ctx, protocol.MsgListen)
	if listen.Provider != "stub" {
		t.Errorf("listen named provider %q, want stub", listen.Provider)
	}

	// Stream three frames of microphone audio.
	sess := waitForSession(t, p)
	up := make([]byte, protocol.DeviceFrameBytes)
	for i := range up {
		up[i] = byte(i)
	}
	for seq := 0; seq < 3; seq++ {
		frame := protocol.Encode(protocol.Frame{
			Type: protocol.FrameAudioUp, Seq: uint16(seq), Payload: up,
		})
		if err := ws.Write(ctx, websocket.MessageBinary, frame); err != nil {
			t.Fatal(err)
		}
	}
	waitFor(t, "uplink audio to reach the provider", func() bool {
		return sess.uplinkBytes() == 3*protocol.DeviceFrameBytes
	})

	// The service replies.
	sess.events <- provider.Event{Kind: provider.EventTurnStart}
	expectControl(t, ws, ctx, protocol.MsgSpeakBegin)

	// Two and a half frames' worth, to prove the tail is not dropped.
	reply := make([]byte, protocol.DeviceFrameBytes*2+320)
	for i := range reply {
		reply[i] = byte(i % 251)
	}
	sess.events <- provider.Event{Kind: provider.EventAudio, Audio: reply}
	sess.events <- provider.Event{
		Kind: provider.EventTranscript, Role: provider.RoleAssistant, Text: "it is foggy",
	}
	sess.events <- provider.Event{Kind: provider.EventTurnEnd}

	_, got := expectControl(t, ws, ctx, protocol.MsgSpeakEnd)

	// The last partial frame is padded with silence rather than truncated, so
	// the robot always receives whole frames.
	wantLen := protocol.DeviceFrameBytes * 3
	if len(got) != wantLen {
		t.Fatalf("received %d bytes of audio, want %d", len(got), wantLen)
	}
	if string(got[:len(reply)]) != string(reply) {
		t.Error("reply audio came back altered")
	}
	for _, b := range got[len(reply):] {
		if b != 0 {
			t.Fatal("the final frame was padded with something other than silence")
		}
	}

	waitFor(t, "the device to leave the turn", func() bool {
		for _, d := range hub.Devices() {
			if d.ID == "marvin-test" {
				return !d.InTurn
			}
		}
		return false
	})
}

// A turn_end from the robot must reach the provider: a device that decides for
// itself that the user has stopped talking has no other way to say so.
func TestTurnEndCommits(t *testing.T) {
	p := &stubProvider{}
	ws, _, ctx := testRig(t, p)

	sendMsg(t, ws, ctx, protocol.Message{Type: protocol.MsgHello, DeviceID: "marvin-test"})
	if _, _, err := readUntilReady(ws, ctx); err != nil {
		t.Fatal(err)
	}
	sendMsg(t, ws, ctx, protocol.Message{Type: protocol.MsgWake})
	expectControl(t, ws, ctx, protocol.MsgListen)

	sess := waitForSession(t, p)
	sendMsg(t, ws, ctx, protocol.Message{Type: protocol.MsgTurnEnd})

	waitFor(t, "the turn to be committed", func() bool {
		sess.mu.Lock()
		defer sess.mu.Unlock()
		return sess.committed
	})
}

// A robot left waiting for a reply that is never coming is worse than one told
// the provider failed: it holds its microphone open indefinitely.
func TestProviderFailureReleasesTheRobot(t *testing.T) {
	p := &stubProvider{dialErr: errors.New("no credit")}
	ws, _, ctx := testRig(t, p)

	sendMsg(t, ws, ctx, protocol.Message{Type: protocol.MsgHello, DeviceID: "marvin-test"})
	if _, _, err := readUntilReady(ws, ctx); err != nil {
		t.Fatal(err)
	}
	sendMsg(t, ws, ctx, protocol.Message{Type: protocol.MsgWake})

	msg, _ := expectControl(t, ws, ctx, protocol.MsgError)
	if !strings.Contains(msg.Message, "no credit") {
		t.Errorf("error message was %q, expected it to name the cause", msg.Message)
	}
	expectControl(t, ws, ctx, protocol.MsgSpeakEnd)
}

// Streaming audio into a session that was never configured is the failure this
// guards against.
func TestFirstMessageMustBeHello(t *testing.T) {
	ws, _, ctx := testRig(t, &stubProvider{})

	sendMsg(t, ws, ctx, protocol.Message{Type: protocol.MsgWake})
	if _, _, err := ws.Read(ctx); err == nil {
		t.Fatal("the server accepted a connection that did not start with hello")
	}
}

func TestUnauthorizedIsRejected(t *testing.T) {
	h := &Handler{
		Hub:  NewHub(),
		Auth: func(string) (string, error) { return "", ErrUnauthorized },
	}
	srv := httptest.NewServer(h)
	defer srv.Close()

	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()

	_, resp, err := websocket.Dial(ctx, "ws"+strings.TrimPrefix(srv.URL, "http"), nil)
	if err == nil {
		t.Fatal("a connection with no token was accepted")
	}
	if resp == nil || resp.StatusCode != http.StatusUnauthorized {
		t.Fatalf("got status %v, want 401", resp)
	}
}

// --- helpers ---------------------------------------------------------------

func readUntilReady(ws *websocket.Conn, ctx context.Context) (protocol.Message, []byte, error) {
	_, data, err := ws.Read(ctx)
	if err != nil {
		return protocol.Message{}, nil, err
	}
	msg, err := protocol.DecodeMessage(data)
	if err != nil {
		return protocol.Message{}, nil, err
	}
	if msg.Type != protocol.MsgReady {
		return msg, nil, errors.New("expected ready, got " + string(msg.Type))
	}
	return msg, nil, nil
}

func waitForSession(t *testing.T, p *stubProvider) *stubSession {
	t.Helper()
	var s *stubSession
	waitFor(t, "the provider session to open", func() bool {
		s = p.current()
		return s != nil
	})
	return s
}

func waitFor(t *testing.T, what string, cond func() bool) {
	t.Helper()
	deadline := time.Now().Add(3 * time.Second)
	for time.Now().Before(deadline) {
		if cond() {
			return
		}
		time.Sleep(2 * time.Millisecond)
	}
	t.Fatalf("timed out waiting for %s", what)
}
