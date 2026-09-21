package device

import (
	"context"
	"errors"
	"fmt"
	"log"
	"sync"
	"time"

	"github.com/coder/websocket"

	"github.com/spedemon/marvin/server/harness"
	"github.com/spedemon/marvin/server/protocol"
	"github.com/spedemon/marvin/server/provider"
)

// ConfigFunc resolves the provider to use, at the moment a conversation starts
// rather than when the robot connected.
//
// That timing is the point: a robot holds its connection open for as long as it
// has power, so a provider changed in the web controller would otherwise not
// take effect until someone power-cycled it.
type ConfigFunc func() (provider.Provider, provider.Config, error)

// conn is one robot's connection.
//
// Two goroutines touch it: the read loop, which owns the socket's read side and
// drives the provider session, and a per-turn pump that carries provider events
// through the harness and back down to the robot. Everything shared between
// them is behind a mutex, and the socket's write side behind its own.
type conn struct {
	ws       *websocket.Conn
	deviceID string
	hub      *Hub
	chain    harness.Chain
	config   ConfigFunc

	writeMu sync.Mutex
	seq     uint16
	frame   []byte // reused encode buffer
	pending []byte // downlink audio not yet a whole frame

	mu         sync.Mutex
	sess       provider.Session
	cancelTurn context.CancelFunc
	turnDone   chan struct{}
}

// run drives the connection until the robot goes away or the context ends.
func (c *conn) run(ctx context.Context) error {
	defer c.endTurn()

	hello, err := c.readHello(ctx)
	if err != nil {
		return err
	}
	if hello.Codec != "" && hello.Codec != protocol.CodecPCM16 {
		// The codec is negotiated rather than assumed so that Opus can be added
		// to the firmware without a flag day. Until it is, anything else is a
		// mismatch worth saying out loud.
		return fmt.Errorf("device %s offers codec %q, which this server cannot decode", c.deviceID, hello.Codec)
	}
	if hello.SampleRate != 0 && hello.SampleRate != protocol.DeviceSampleRate {
		return fmt.Errorf("device %s offers %d Hz audio, expected %d",
			c.deviceID, hello.SampleRate, protocol.DeviceSampleRate)
	}

	c.hub.connected(c.deviceID, hello.Firmware, c)
	defer c.hub.disconnected(c.deviceID)

	if err := c.Control(protocol.Message{
		Type:      protocol.MsgReady,
		SessionID: c.deviceID,
	}); err != nil {
		return err
	}

	go c.keepalive(ctx)

	for {
		typ, data, err := c.ws.Read(ctx)
		if err != nil {
			return closeReason(err)
		}
		switch typ {
		case websocket.MessageBinary:
			c.handleFrame(data)
		case websocket.MessageText:
			msg, err := protocol.DecodeMessage(data)
			if err != nil {
				log.Printf("device %s: undecodable control message: %v", c.deviceID, err)
				continue
			}
			c.handleControl(ctx, msg)
		}
	}
}

// readHello waits for the opening message. A connection that starts with
// anything else is rejected rather than tolerated: the alternative is a robot
// streaming audio into a session that was never configured.
func (c *conn) readHello(ctx context.Context) (protocol.Message, error) {
	helloCtx, cancel := context.WithTimeout(ctx, 10*time.Second)
	defer cancel()

	typ, data, err := c.ws.Read(helloCtx)
	if err != nil {
		return protocol.Message{}, fmt.Errorf("waiting for hello: %w", err)
	}
	if typ != websocket.MessageText {
		return protocol.Message{}, errors.New("first message was not a control message")
	}
	msg, err := protocol.DecodeMessage(data)
	if err != nil {
		return protocol.Message{}, fmt.Errorf("decoding hello: %w", err)
	}
	if msg.Type != protocol.MsgHello {
		return protocol.Message{}, fmt.Errorf("first message was %q, expected hello", msg.Type)
	}
	return msg, nil
}

func (c *conn) handleFrame(data []byte) {
	f, err := protocol.Decode(data)
	if err != nil {
		log.Printf("device %s: bad frame: %v", c.deviceID, err)
		return
	}
	if f.Type != protocol.FrameAudioUp {
		return // the robot has no business sending anything else
	}

	c.mu.Lock()
	sess := c.sess
	c.mu.Unlock()
	if sess == nil {
		// Audio outside a turn. Normal for a frame or two after a reply
		// starts, because the robot's own stop and ours cross in flight.
		return
	}
	if err := sess.SendAudio(f.Payload); err != nil {
		log.Printf("device %s: sending audio to provider: %v", c.deviceID, err)
	}
}

func (c *conn) handleControl(ctx context.Context, msg protocol.Message) {
	switch msg.Type {
	case protocol.MsgWake:
		// The robot has already nodded by the time this arrives. It moves on
		// its own evidence so that the acknowledgement is immediate rather than
		// a network round trip late.
		c.startTurn(ctx)

	case protocol.MsgTurnEnd:
		c.mu.Lock()
		sess := c.sess
		c.mu.Unlock()
		if sess != nil {
			if err := sess.CommitTurn(); err != nil {
				log.Printf("device %s: committing turn: %v", c.deviceID, err)
			}
		}

	case protocol.MsgState:
		c.hub.update(c.deviceID, func(s *State) {
			s.RSSI = msg.RSSI
			s.BatteryMV = msg.BatteryMV
		})

	case protocol.MsgPong:
		c.hub.update(c.deviceID, func(*State) {})
	}
}

// startTurn opens a provider session and pumps its events through the harness.
//
// The session lives for one exchange and is closed when the reply ends. Holding
// it open between questions would save a few hundred milliseconds of dialling
// and cost a billed, idle connection for every robot that is merely switched
// on — the wrong trade for something that sits on a shelf most of the day.
func (c *conn) startTurn(ctx context.Context) {
	c.endTurn()

	p, cfg, err := c.config()
	if err != nil {
		c.fail(fmt.Errorf("no usable AI provider: %w", err))
		return
	}
	cfg.Tools = c.chain.Tools()

	dialCtx, cancelDial := context.WithTimeout(ctx, 15*time.Second)
	sess, err := p.Dial(dialCtx, cfg)
	cancelDial()
	if err != nil {
		c.fail(fmt.Errorf("%s: %w", p.Name(), err))
		return
	}

	turnCtx, cancel := context.WithCancel(ctx)
	done := make(chan struct{})

	c.mu.Lock()
	c.sess = sess
	c.cancelTurn = cancel
	c.turnDone = done
	c.mu.Unlock()

	c.hub.update(c.deviceID, func(s *State) {
		s.Provider = p.Name()
		s.InTurn = true
	})
	if err := c.Control(protocol.Message{Type: protocol.MsgListen, Provider: p.Name()}); err != nil {
		log.Printf("device %s: sending listen: %v", c.deviceID, err)
	}

	go c.pump(turnCtx, sess, done)
}

// pump carries provider events through the harness until the turn ends.
func (c *conn) pump(ctx context.Context, sess provider.Session, done chan struct{}) {
	defer close(done)
	defer sess.Close()

	for {
		select {
		case <-ctx.Done():
			return
		case ev, ok := <-sess.Events():
			if !ok {
				return
			}
			// The tail of a reply has to be on the wire before a handler
			// announces the turn is over, because speak_end is what makes the
			// robot mute its amplifier. Doing this after the chain clips the
			// last few milliseconds of every sentence.
			switch ev.Kind {
			case provider.EventTurnEnd:
				c.flushAudio()
			case provider.EventInterrupted:
				// Someone talked over the robot. What is still queued is the
				// rest of a sentence nobody wants to hear finished.
				c.discardAudio()
			}

			if err := c.chain.OnEvent(ctx, ev, c); err != nil {
				log.Printf("device %s: handler: %v", c.deviceID, err)
				return
			}
			switch ev.Kind {
			case provider.EventTurnEnd, provider.EventInterrupted:
				c.hub.update(c.deviceID, func(s *State) { s.InTurn = false })
				return
			case provider.EventError:
				c.hub.update(c.deviceID, func(s *State) { s.InTurn = false })
				return
			}
		}
	}
}

// endTurn stops any conversation in flight and waits for its pump to finish, so
// that a new turn never overlaps the old one's audio.
func (c *conn) endTurn() {
	c.mu.Lock()
	cancel, done := c.cancelTurn, c.turnDone
	c.sess, c.cancelTurn, c.turnDone = nil, nil, nil
	c.mu.Unlock()

	if cancel != nil {
		cancel()
	}
	if done != nil {
		<-done
	}
}

func (c *conn) fail(err error) {
	log.Printf("device %s: %v", c.deviceID, err)
	c.Observe(harness.Observation{Kind: "error", Text: err.Error(), At: time.Now()})
	// Release the robot: it is holding its microphone open waiting for a reply
	// that is not coming.
	_ = c.Control(protocol.Message{Type: protocol.MsgError, Message: err.Error()})
	_ = c.Control(protocol.Message{Type: protocol.MsgSpeakEnd})
}

// keepalive both proves the link is alive and stops intermediaries from
// reaping an idle connection. A robot waiting for a wake word sends nothing for
// hours at a time, which many load balancers read as abandoned.
func (c *conn) keepalive(ctx context.Context) {
	t := time.NewTicker(30 * time.Second)
	defer t.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-t.C:
			pingCtx, cancel := context.WithTimeout(ctx, 10*time.Second)
			err := c.ws.Ping(pingCtx)
			cancel()
			if err != nil {
				return // the read loop will see the same failure and clean up
			}
		}
	}
}

// closeReason turns an ordinary hang-up into a nil error, so that a robot
// losing power does not read as a server fault in the log.
func closeReason(err error) error {
	switch websocket.CloseStatus(err) {
	case websocket.StatusNormalClosure, websocket.StatusGoingAway:
		return nil
	}
	if errors.Is(err, context.Canceled) {
		return nil
	}
	return err
}
