// Package protocol defines the wire format Marvin and the backend share.
//
// One WebSocket connection carries both halves of a conversation: binary frames
// are audio, text frames are JSON control messages. Splitting them this way
// means audio never pays for base64 and control never has to be parsed out of a
// byte stream.
//
// The format is deliberately transport-agnostic. Nothing here knows it is
// riding on a WebSocket, so moving to MQTT later is a change in one file at
// each end rather than a redesign.
package protocol

import (
	"encoding/binary"
	"errors"
	"fmt"
)

// FrameType identifies what a binary frame carries. Direction is implicit in
// the type rather than the connection, so a capture of the stream reads
// correctly without knowing which end recorded it.
type FrameType uint8

const (
	// FrameAudioUp is microphone audio travelling device -> server.
	FrameAudioUp FrameType = 0x01
	// FrameAudioDown is speaker audio travelling server -> device.
	FrameAudioDown FrameType = 0x02
)

// Flag bits. None are used yet; they exist so that adding one later does not
// need a version negotiation.
const (
	// FlagLast marks the final audio frame of a turn. Advisory: the control
	// message is what actually ends a turn.
	FlagLast uint8 = 1 << 0
)

const (
	// HeaderSize is [type:1][flags:1][seq:2].
	HeaderSize = 4

	// MaxFrameSize bounds what will be read off the wire. Audio frames are 644
	// bytes today (20 ms of 16 kHz mono PCM16, plus the header); the ceiling is
	// far above that so a future codec or a longer frame does not need a
	// protocol change, but still low enough that a hostile peer cannot make the
	// server allocate.
	MaxFrameSize = 4096
)

// ErrShortFrame means the bytes received cannot contain a header.
var ErrShortFrame = errors.New("protocol: frame shorter than header")

// Frame is one binary message.
type Frame struct {
	Type    FrameType
	Flags   uint8
	Seq     uint16
	Payload []byte
}

// AppendFrame encodes f onto dst and returns the extended slice. It appends
// rather than allocating so a sender can reuse one buffer for every frame — at
// fifty frames a second per device, that matters.
func AppendFrame(dst []byte, f Frame) []byte {
	var hdr [HeaderSize]byte
	hdr[0] = byte(f.Type)
	hdr[1] = f.Flags
	binary.BigEndian.PutUint16(hdr[2:], f.Seq)
	dst = append(dst, hdr[:]...)
	return append(dst, f.Payload...)
}

// Encode returns f as a new byte slice.
func Encode(f Frame) []byte {
	return AppendFrame(make([]byte, 0, HeaderSize+len(f.Payload)), f)
}

// Decode parses a binary frame. The returned Payload aliases b, so a caller
// that keeps it beyond the life of the read buffer must copy it.
func Decode(b []byte) (Frame, error) {
	if len(b) < HeaderSize {
		return Frame{}, ErrShortFrame
	}
	if len(b) > MaxFrameSize {
		return Frame{}, fmt.Errorf("protocol: frame of %d bytes exceeds the %d byte limit", len(b), MaxFrameSize)
	}
	return Frame{
		Type:    FrameType(b[0]),
		Flags:   b[1],
		Seq:     binary.BigEndian.Uint16(b[2:4]),
		Payload: b[HeaderSize:],
	}, nil
}

func (t FrameType) String() string {
	switch t {
	case FrameAudioUp:
		return "audio_up"
	case FrameAudioDown:
		return "audio_down"
	default:
		return fmt.Sprintf("unknown(0x%02x)", uint8(t))
	}
}
