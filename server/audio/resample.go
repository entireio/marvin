// Package audio converts between the robot's audio format and whatever the AI
// provider wants.
//
// The robot runs at 16 kHz in both directions because its microphone and
// amplifier share one I2S peripheral, and therefore one clock. The providers do
// not agree with it or with each other: OpenAI Realtime is 24 kHz both ways,
// Gemini Live is 16 kHz in and 24 kHz out. Rather than push that mismatch onto
// the firmware, every conversion happens here.
package audio

import (
	"fmt"
	"math"
)

// tapsPerPhase sets the quality and cost of the filter. Sixteen gives a
// transition band narrow enough that speech is unaffected, at a few hundred
// multiply-accumulates per output sample — trivial next to the network and the
// model behind it.
const tapsPerPhase = 16

// Resampler converts a stream between two sample rates.
//
// It is stateful on purpose. Audio arrives in 20 ms chunks, and a filter that
// forgot its history between chunks would put a discontinuity at every
// boundary — fifty clicks a second, which is exactly what naive per-chunk
// resampling sounds like. One Resampler belongs to one direction of one
// session, and is not safe for concurrent use.
type Resampler struct {
	l, m  int       // interpolation and decimation factors, coprime
	taps  []float32 // prototype low-pass, designed at l * inRate
	hist  []int16   // the tapsPerPhase-1 most recent input samples
	phase int       // (output index * m) mod l
	next  int       // input index the next output needs, relative to the next chunk

	scratch []int16 // reused across calls so Process does not allocate history
	out     []int16
}

// NewResampler builds a converter from inRate to outRate. Equal rates give a
// passthrough, which costs nothing and keeps callers from having to special-case
// Gemini's already-matching 16 kHz input.
func NewResampler(inRate, outRate int) (*Resampler, error) {
	if inRate <= 0 || outRate <= 0 {
		return nil, fmt.Errorf("audio: rates must be positive, got %d -> %d", inRate, outRate)
	}
	g := gcd(inRate, outRate)
	l, m := outRate/g, inRate/g

	r := &Resampler{l: l, m: m}
	if l == 1 && m == 1 {
		return r, nil // passthrough
	}

	// Cut off at the lower of the two Nyquist limits, expressed against the
	// interpolated rate. Upsampling needs it to suppress the images that
	// zero-stuffing creates; downsampling needs it to stop everything above the
	// new Nyquist folding back into the audible band as harshness.
	fc := 0.5 / float64(max(l, m))
	n := tapsPerPhase * l
	centre := float64(n-1) / 2

	r.taps = make([]float32, n)
	for i := 0; i < n; i++ {
		x := float64(i) - centre
		// Hamming window on a sinc. The gain of l compensates for the l-1
		// zeros inserted between every pair of input samples.
		w := 0.54 - 0.46*math.Cos(2*math.Pi*float64(i)/float64(n-1))
		r.taps[i] = float32(float64(l) * 2 * fc * sinc(2*fc*x) * w)
	}
	r.hist = make([]int16, tapsPerPhase-1)
	return r, nil
}

// Rates reports the interpolation and decimation factors, for tests and logs.
func (r *Resampler) Rates() (l, m int) { return r.l, r.m }

// Reset clears filter history. Call it between turns, not between chunks.
func (r *Resampler) Reset() {
	for i := range r.hist {
		r.hist[i] = 0
	}
	r.phase = 0
	r.next = 0
}

// Process converts one chunk. The returned slice is reused on the next call, so
// a caller that keeps it must copy.
func (r *Resampler) Process(in []int16) []int16 {
	if r.l == 1 && r.m == 1 {
		return in
	}
	if len(in) == 0 {
		return nil
	}

	// Index i below is relative to the start of in, so negative values reach
	// back into the previous chunk. Laying history and input out contiguously
	// makes that a plain offset instead of a branch in the inner loop.
	pad := tapsPerPhase - 1
	r.scratch = append(r.scratch[:0], r.hist...)
	r.scratch = append(r.scratch, in...)
	all := r.scratch

	est := (len(in)*r.l)/r.m + 2
	r.out = r.out[:0]
	if cap(r.out) < est {
		r.out = make([]int16, 0, est)
	}

	// Each output sample is one phase of the prototype filter applied to the
	// last tapsPerPhase input samples. Stepping phase by m and carrying into
	// the input index keeps this exact without ever forming output_index * m,
	// which would overflow on a long-lived session.
	for r.next < len(in) {
		var acc float32
		for j := 0; j < tapsPerPhase; j++ {
			acc += r.taps[r.phase+j*r.l] * float32(all[r.next-j+pad])
		}
		r.out = append(r.out, clamp16(acc))

		r.phase += r.m
		for r.phase >= r.l {
			r.phase -= r.l
			r.next++
		}
	}
	r.next -= len(in)

	// Carry the tail forward as the next chunk's history.
	if len(all) >= pad {
		copy(r.hist, all[len(all)-pad:])
	} else {
		// A chunk shorter than the filter's memory: shift what we have in.
		copy(r.hist, r.hist[len(all):])
		copy(r.hist[pad-len(all):], all)
	}
	return r.out
}

func sinc(x float64) float64 {
	if x == 0 {
		return 1
	}
	px := math.Pi * x
	return math.Sin(px) / px
}

func clamp16(v float32) int16 {
	// Filter overshoot on a loud passage can exceed full scale. Clipping is
	// the right answer; wrapping would turn a slightly hot sample into a bang.
	if v > math.MaxInt16 {
		return math.MaxInt16
	}
	if v < math.MinInt16 {
		return math.MinInt16
	}
	return int16(v)
}

func gcd(a, b int) int {
	for b != 0 {
		a, b = b, a%b
	}
	return a
}
