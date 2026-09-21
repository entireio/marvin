package audio

import (
	"encoding/binary"
	"errors"
	"fmt"
	"io"
)

// Just enough WAV to get test audio in and out.
//
// This exists so that the whole backend can be exercised from a file — speak
// into a recorder, run it through, listen to what comes back — without a robot
// on the desk. That loop is worth far more than the eighty lines it costs.

// ReadWAV16 reads a RIFF/WAVE file and returns mono PCM16 at rate, converting
// from whatever the file happens to be.
func ReadWAV16(r io.Reader, rate int) ([]int16, error) {
	raw, err := io.ReadAll(r)
	if err != nil {
		return nil, err
	}
	if len(raw) < 12 || string(raw[0:4]) != "RIFF" || string(raw[8:12]) != "WAVE" {
		return nil, errors.New("audio: not a RIFF/WAVE file")
	}

	var (
		channels   int
		sampleRate int
		bits       int
		data       []byte
		haveFmt    bool
	)

	// Chunks are walked rather than assumed in order: real files carry LIST and
	// fact chunks between fmt and data, and skipping straight to offset 44
	// silently reads metadata as audio.
	for off := 12; off+8 <= len(raw); {
		id := string(raw[off : off+4])
		size := int(binary.LittleEndian.Uint32(raw[off+4 : off+8]))
		body := off + 8
		if size < 0 || body+size > len(raw) {
			size = len(raw) - body // tolerate a truncated final chunk
		}
		switch id {
		case "fmt ":
			if size < 16 {
				return nil, errors.New("audio: fmt chunk is too short")
			}
			format := binary.LittleEndian.Uint16(raw[body : body+2])
			if format != 1 && format != 0xFFFE { // PCM, or extensible-wrapping-PCM
				return nil, fmt.Errorf("audio: WAV format %d is not PCM", format)
			}
			channels = int(binary.LittleEndian.Uint16(raw[body+2 : body+4]))
			sampleRate = int(binary.LittleEndian.Uint32(raw[body+4 : body+8]))
			bits = int(binary.LittleEndian.Uint16(raw[body+14 : body+16]))
			haveFmt = true
		case "data":
			data = raw[body : body+size]
		}
		off = body + size
		if size%2 == 1 {
			off++ // chunks are word-aligned
		}
	}

	if !haveFmt || data == nil {
		return nil, errors.New("audio: WAV file has no fmt or data chunk")
	}
	if bits != 16 {
		return nil, fmt.Errorf("audio: %d-bit WAV; only 16-bit is supported", bits)
	}
	if channels < 1 {
		return nil, errors.New("audio: WAV file claims no channels")
	}

	samples := AppendInt16(nil, data)
	if channels > 1 {
		mono := make([]int16, 0, len(samples)/channels)
		for i := 0; i+channels <= len(samples); i += channels {
			var sum int
			for c := 0; c < channels; c++ {
				sum += int(samples[i+c])
			}
			mono = append(mono, int16(sum/channels))
		}
		samples = mono
	}

	if sampleRate != rate {
		rs, err := NewResampler(sampleRate, rate)
		if err != nil {
			return nil, err
		}
		samples = append([]int16(nil), rs.Process(samples)...)
	}
	return samples, nil
}

// WriteWAV16 writes mono PCM16 as a RIFF/WAVE file.
func WriteWAV16(w io.Writer, samples []int16, rate int) error {
	const (
		headerSize = 44
		bits       = 16
		channels   = 1
	)
	dataSize := len(samples) * 2

	h := make([]byte, 0, headerSize)
	h = append(h, "RIFF"...)
	h = binary.LittleEndian.AppendUint32(h, uint32(headerSize-8+dataSize))
	h = append(h, "WAVEfmt "...)
	h = binary.LittleEndian.AppendUint32(h, 16)                           // fmt chunk size
	h = binary.LittleEndian.AppendUint16(h, 1)                            // PCM
	h = binary.LittleEndian.AppendUint16(h, channels)                     //
	h = binary.LittleEndian.AppendUint32(h, uint32(rate))                 //
	h = binary.LittleEndian.AppendUint32(h, uint32(rate*channels*bits/8)) // byte rate
	h = binary.LittleEndian.AppendUint16(h, uint16(channels*bits/8))      // block align
	h = binary.LittleEndian.AppendUint16(h, bits)                         //
	h = append(h, "data"...)
	h = binary.LittleEndian.AppendUint32(h, uint32(dataSize))

	if _, err := w.Write(h); err != nil {
		return err
	}
	_, err := w.Write(AppendBytes(make([]byte, 0, dataSize), samples))
	return err
}
