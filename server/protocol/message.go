package protocol

import "encoding/json"

// MsgType is the discriminator on every JSON control message.
type MsgType string

// Device -> server.
const (
	// MsgHello opens a session. Always the first message; the server replies
	// with MsgReady or closes the connection.
	MsgHello MsgType = "hello"
	// MsgWake says the wake word fired. The device has already played its
	// acknowledgement gesture by the time this arrives — the head moves on
	// local evidence, never on a round trip.
	MsgWake MsgType = "wake"
	// MsgTurnEnd says the device has stopped sending audio for this turn.
	MsgTurnEnd MsgType = "turn_end"
	// MsgState is unsolicited telemetry.
	MsgState MsgType = "state"
	// MsgPong answers MsgPing.
	MsgPong MsgType = "pong"
)

// Server -> device.
const (
	// MsgReady accepts the session.
	MsgReady MsgType = "ready"
	// MsgListen tells the device to start streaming microphone audio.
	MsgListen MsgType = "listen"
	// MsgSpeakBegin warns that audio frames are coming. The device stops its
	// uplink here: without echo cancellation an open microphone would have
	// Marvin interrupting itself.
	MsgSpeakBegin MsgType = "speak_begin"
	// MsgSpeakEnd says the reply is finished and the device may listen again.
	MsgSpeakEnd MsgType = "speak_end"
	// MsgAct asks the robot to do something physical. This is the seam the
	// intent harness will use; today only the controller UI produces it.
	MsgAct MsgType = "act"
	// MsgError reports a failure. Advisory — the server closes the connection
	// if the session cannot continue.
	MsgError MsgType = "error"
	// MsgPing checks liveness.
	MsgPing MsgType = "ping"
)

// Message is every control message in one struct.
//
// A struct per type would be tidier in Go and considerably worse in C++: the
// firmware parses these with ArduinoJson on a microcontroller, and one flat
// shape with optional fields is far cheaper there than a discriminated union.
// Both ends are ours, so the cost of the compromise is bounded.
type Message struct {
	Type MsgType `json:"t"`

	// hello
	DeviceID   string `json:"device_id,omitempty"`
	Firmware   string `json:"fw,omitempty"`
	Codec      string `json:"codec,omitempty"` // "pcm16" today; the hook for Opus
	SampleRate int    `json:"sample_rate,omitempty"`

	// ready
	SessionID string `json:"session_id,omitempty"`
	Provider  string `json:"provider,omitempty"`

	// act — exactly one of these. Cmd is a raw firmware command ("T40"), which
	// reuses the interpreter the serial and BLE paths already share; Gesture is
	// a named movement ("nod"), which is what a model-driven action should
	// emit, because names survive a change to the robot's geometry.
	Cmd     string `json:"cmd,omitempty"`
	Gesture string `json:"gesture,omitempty"`

	// state
	RSSI      int `json:"rssi,omitempty"`
	BatteryMV int `json:"battery_mv,omitempty"`

	// error
	Message string `json:"message,omitempty"`

	// Extra carries anything a handler wants to add without widening this
	// struct. The firmware ignores fields it does not know.
	Extra json.RawMessage `json:"extra,omitempty"`
}

// EncodeMessage marshals a control message.
func EncodeMessage(m Message) ([]byte, error) { return json.Marshal(m) }

// DecodeMessage parses a control message.
func DecodeMessage(b []byte) (Message, error) {
	var m Message
	err := json.Unmarshal(b, &m)
	return m, err
}

// Audio constants for the device link. Capture and playback share an I2S
// peripheral on the robot and therefore a clock, so one rate serves both
// directions and the backend resamples for whatever the AI provider wants.
const (
	// DeviceSampleRate is the rate on the wire to and from the robot.
	DeviceSampleRate = 16000
	// DeviceFrameMillis is the span of audio in one frame.
	DeviceFrameMillis = 20
	// DeviceFrameSamples is DeviceFrameMillis of mono audio.
	DeviceFrameSamples = DeviceSampleRate / 1000 * DeviceFrameMillis // 320
	// DeviceFrameBytes is DeviceFrameSamples as PCM16.
	DeviceFrameBytes = DeviceFrameSamples * 2 // 640
	// CodecPCM16 is the only codec the firmware speaks today.
	CodecPCM16 = "pcm16"
)
