package device

import (
	"context"
	"errors"
	"time"

	"github.com/coder/websocket"

	"github.com/spedemon/marvin/server/harness"
	"github.com/spedemon/marvin/server/protocol"
)

// conn implements harness.Emitter. These four methods are the only way a
// handler can affect the robot or the web controller.

// writeTimeout bounds a single frame write. A robot whose Wi-Fi has dropped but
// whose TCP connection has not yet noticed will otherwise absorb the whole
// reply into a send buffer and block the pump.
const writeTimeout = 10 * time.Second

// Audio queues synthesized speech for the robot.
//
// The provider hands over chunks of whatever length suits it; the robot wants
// exactly one 20 ms frame at a time, because that is what its I2S buffer is
// sized for. The remainder is carried between calls rather than padded, since
// padding mid-reply would insert a click at every chunk boundary.
func (c *conn) Audio(pcm []byte) error {
	c.writeMu.Lock()
	defer c.writeMu.Unlock()

	c.pending = append(c.pending, pcm...)
	for len(c.pending) >= protocol.DeviceFrameBytes {
		if err := c.writeFrameLocked(c.pending[:protocol.DeviceFrameBytes], 0); err != nil {
			return err
		}
		c.pending = c.pending[protocol.DeviceFrameBytes:]
	}
	// Move the tail to the front so the slice does not creep forward through an
	// ever-growing backing array over a long reply.
	c.pending = append(c.pending[:0:0], c.pending...)
	return nil
}

// flushAudio sends the last partial frame of a reply, padded with silence, and
// marks it as the end of the turn.
func (c *conn) flushAudio() {
	c.writeMu.Lock()
	defer c.writeMu.Unlock()

	if len(c.pending) == 0 {
		return
	}
	frame := make([]byte, protocol.DeviceFrameBytes)
	copy(frame, c.pending)
	c.pending = c.pending[:0]
	if err := c.writeFrameLocked(frame, protocol.FlagLast); err != nil {
		return // the read loop will notice the same failure
	}
}

// discardAudio drops the queued tail of a reply that has been cut short.
func (c *conn) discardAudio() {
	c.writeMu.Lock()
	defer c.writeMu.Unlock()
	c.pending = c.pending[:0]
}

// writeFrameLocked writes one audio frame. Caller holds writeMu.
func (c *conn) writeFrameLocked(payload []byte, flags uint8) error {
	c.frame = protocol.AppendFrame(c.frame[:0], protocol.Frame{
		Type:    protocol.FrameAudioDown,
		Flags:   flags,
		Seq:     c.seq,
		Payload: payload,
	})
	// Wraps every twenty-one minutes of continuous speech, which is what the
	// robot's own loss detection expects.
	c.seq++

	ctx, cancel := context.WithTimeout(context.Background(), writeTimeout)
	defer cancel()
	return c.ws.Write(ctx, websocket.MessageBinary, c.frame)
}

// Control sends a control message to the robot.
func (c *conn) Control(m protocol.Message) error {
	b, err := protocol.EncodeMessage(m)
	if err != nil {
		return err
	}

	c.writeMu.Lock()
	defer c.writeMu.Unlock()

	ctx, cancel := context.WithTimeout(context.Background(), writeTimeout)
	defer cancel()
	return c.ws.Write(ctx, websocket.MessageText, b)
}

// Observe publishes to the web controller. It never reaches the robot and never
// fails: a dropped observation costs a line in a transcript, and making
// handlers deal with that would not improve anything.
func (c *conn) Observe(o harness.Observation) {
	if o.At.IsZero() {
		o.At = time.Now()
	}
	c.hub.observe(c.deviceID, o)
}

// ToolResult answers a tool call so the model can carry on.
func (c *conn) ToolResult(id string, result any) error {
	c.mu.Lock()
	sess := c.sess
	c.mu.Unlock()
	if sess == nil {
		return errors.New("device: tool result with no session in flight")
	}
	return sess.SendToolResult(id, result)
}
