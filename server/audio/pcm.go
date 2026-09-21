package audio

import "encoding/binary"

// Everything on the wire is little-endian PCM16 mono — the robot's I2S format,
// OpenAI Realtime's pcm16 and Gemini Live's raw PCM all agree on that, so the
// only thing that ever needs converting is the rate.

// AppendInt16 decodes little-endian PCM16 bytes onto dst and returns the
// extended slice. An odd trailing byte is dropped: a half sample is not
// recoverable and carrying it would put every later sample out of phase.
func AppendInt16(dst []int16, b []byte) []int16 {
	for i := 0; i+1 < len(b); i += 2 {
		dst = append(dst, int16(binary.LittleEndian.Uint16(b[i:])))
	}
	return dst
}

// AppendBytes encodes samples as little-endian PCM16 onto dst.
func AppendBytes(dst []byte, samples []int16) []byte {
	for _, s := range samples {
		dst = append(dst, byte(uint16(s)), byte(uint16(s)>>8))
	}
	return dst
}

// Converter resamples a PCM16 byte stream. It wraps Resampler with the byte
// packing that every caller would otherwise repeat, and reuses its buffers, so
// the per-frame cost on a busy server is one pass over 640 bytes and no
// allocation.
//
// Like Resampler it is stateful and single-goroutine: one per direction, per
// session.
type Converter struct {
	r      *Resampler
	inBuf  []int16
	outBuf []byte
}

// NewConverter builds a byte-level converter from inRate to outRate.
func NewConverter(inRate, outRate int) (*Converter, error) {
	r, err := NewResampler(inRate, outRate)
	if err != nil {
		return nil, err
	}
	return &Converter{r: r}, nil
}

// Convert resamples one chunk of PCM16. The returned slice is reused on the
// next call; copy it if it must outlive that.
func (c *Converter) Convert(pcm []byte) []byte {
	c.inBuf = AppendInt16(c.inBuf[:0], pcm)
	out := c.r.Process(c.inBuf)
	c.outBuf = AppendBytes(c.outBuf[:0], out)
	return c.outBuf
}

// Reset clears filter history. Between turns, not between chunks.
func (c *Converter) Reset() { c.r.Reset() }

// Passthrough reports whether this converter is a no-op, which is true whenever
// the rates already match.
func (c *Converter) Passthrough() bool {
	l, m := c.r.Rates()
	return l == 1 && m == 1
}
