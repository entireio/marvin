package audio

import (
	"math"
	"testing"
)

// tone generates n samples of a sine at freq Hz, sampled at rate.
func tone(n, rate int, freq float64, amp float64) []int16 {
	out := make([]int16, n)
	for i := range out {
		out[i] = int16(amp * math.Sin(2*math.Pi*freq*float64(i)/float64(rate)))
	}
	return out
}

func rms(s []int16) float64 {
	if len(s) == 0 {
		return 0
	}
	var sum float64
	for _, v := range s {
		sum += float64(v) * float64(v)
	}
	return math.Sqrt(sum / float64(len(s)))
}

// The important property: resampling a stream in chunks must give bit-identical
// output to resampling it in one go. If filter history is not carried across
// chunk boundaries this fails, and in production it is heard as a click fifty
// times a second rather than seen as a test failure.
func TestProcessIsChunkIndependent(t *testing.T) {
	const total = 4800
	in := tone(total, 16000, 440, 8000)

	whole, err := NewResampler(16000, 24000)
	if err != nil {
		t.Fatal(err)
	}
	want := append([]int16(nil), whole.Process(in)...)

	chunked, err := NewResampler(16000, 24000)
	if err != nil {
		t.Fatal(err)
	}
	var got []int16
	for off := 0; off < total; off += 320 { // 20 ms frames, as on the wire
		end := min(off+320, total)
		got = append(got, chunked.Process(in[off:end])...)
	}

	if len(got) != len(want) {
		t.Fatalf("chunked produced %d samples, whole produced %d", len(got), len(want))
	}
	for i := range want {
		if got[i] != want[i] {
			t.Fatalf("sample %d differs: chunked %d, whole %d", i, got[i], want[i])
		}
	}
}

// A tone well inside the passband must survive 16k -> 24k -> 16k intact. This
// is the actual round trip for OpenAI Realtime.
func TestRoundTripPreservesSpeechBand(t *testing.T) {
	const n = 4800
	in := tone(n, 16000, 1000, 8000)

	up, err := NewResampler(16000, 24000)
	if err != nil {
		t.Fatal(err)
	}
	down, err := NewResampler(24000, 16000)
	if err != nil {
		t.Fatal(err)
	}
	mid := append([]int16(nil), up.Process(in)...)
	out := append([]int16(nil), down.Process(mid)...)

	// Both stages delay the signal, so compare at the best alignment rather
	// than assuming a particular group delay.
	bestErr := math.MaxFloat64
	for shift := 0; shift < 64; shift++ {
		var sum float64
		count := 0
		for i := 1000; i+shift < len(out) && i < n-1000; i++ {
			d := float64(out[i+shift]) - float64(in[i])
			sum += d * d
			count++
		}
		if count > 0 {
			if e := math.Sqrt(sum / float64(count)); e < bestErr {
				bestErr = e
			}
		}
	}

	// Against a signal of RMS ~5657, a few percent error is the filter's
	// passband ripple, not a bug.
	if limit := rms(in) * 0.10; bestErr > limit {
		t.Fatalf("round-trip RMS error %.1f exceeds %.1f (input RMS %.1f)", bestErr, limit, rms(in))
	}
}

// Content above the destination Nyquist must be filtered away, not folded back
// into the audible band. Without the low-pass, this 10 kHz tone would come out
// as a loud 6 kHz one.
func TestDownsampleRejectsAboveNyquist(t *testing.T) {
	in := tone(4800, 24000, 10000, 8000)

	r, err := NewResampler(24000, 16000)
	if err != nil {
		t.Fatal(err)
	}
	out := r.Process(in)

	// Skip the filter's start-up transient before measuring.
	if len(out) < 400 {
		t.Fatalf("expected a useful number of output samples, got %d", len(out))
	}
	got, want := rms(out[200:]), rms(in)
	if got > want*0.2 {
		t.Fatalf("10 kHz tone survived downsampling: output RMS %.1f vs input %.1f", got, want)
	}
}

func TestOutputRateRatio(t *testing.T) {
	for _, tc := range []struct{ in, out int }{
		{16000, 24000},
		{24000, 16000},
	} {
		r, err := NewResampler(tc.in, tc.out)
		if err != nil {
			t.Fatal(err)
		}
		const n = 16000
		got := len(r.Process(tone(n, tc.in, 500, 4000)))
		want := n * tc.out / tc.in
		if diff := got - want; diff > 2 || diff < -2 {
			t.Errorf("%d -> %d: got %d samples, want about %d", tc.in, tc.out, got, want)
		}
	}
}

// Gemini takes 16 kHz input directly, so that direction must cost nothing.
func TestEqualRatesArePassthrough(t *testing.T) {
	c, err := NewConverter(16000, 16000)
	if err != nil {
		t.Fatal(err)
	}
	if !c.Passthrough() {
		t.Fatal("16 kHz -> 16 kHz should be a passthrough")
	}
	in := AppendBytes(nil, tone(320, 16000, 440, 8000))
	if got := c.Convert(in); string(got) != string(in) {
		t.Fatal("passthrough converter altered the samples")
	}
}

func TestAppendRoundTrip(t *testing.T) {
	want := []int16{0, 1, -1, 32767, -32768, 1234}
	got := AppendInt16(nil, AppendBytes(nil, want))
	if len(got) != len(want) {
		t.Fatalf("got %d samples, want %d", len(got), len(want))
	}
	for i := range want {
		if got[i] != want[i] {
			t.Fatalf("sample %d: got %d, want %d", i, got[i], want[i])
		}
	}
}

// A trailing odd byte must be dropped, not half-decoded: carrying it would put
// every subsequent sample out of phase by one byte.
func TestAppendInt16DropsOddTrailingByte(t *testing.T) {
	if got := AppendInt16(nil, []byte{0x01, 0x02, 0x03}); len(got) != 1 {
		t.Fatalf("got %d samples from 3 bytes, want 1", len(got))
	}
}
