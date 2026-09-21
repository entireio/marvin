package audio

import (
	"bytes"
	"math"
	"testing"
)

// A WAV written here has to be readable here, and a file recorded at some other
// rate has to arrive at the robot's. Both matter because fakedevice is how the
// backend gets tested at all.
func TestWAVRoundTrip(t *testing.T) {
	want := tone(1600, 16000, 440, 8000)

	var buf bytes.Buffer
	if err := WriteWAV16(&buf, want, 16000); err != nil {
		t.Fatal(err)
	}
	got, err := ReadWAV16(&buf, 16000)
	if err != nil {
		t.Fatal(err)
	}
	if len(got) != len(want) {
		t.Fatalf("read back %d samples, wrote %d", len(got), len(want))
	}
	for i := range want {
		if got[i] != want[i] {
			t.Fatalf("sample %d: got %d, want %d", i, got[i], want[i])
		}
	}
}

// A file at 44.1 kHz is the normal case for anything recorded on a laptop.
func TestWAVConvertsRateOnRead(t *testing.T) {
	var buf bytes.Buffer
	if err := WriteWAV16(&buf, tone(44100, 44100, 440, 8000), 44100); err != nil {
		t.Fatal(err)
	}
	got, err := ReadWAV16(&buf, 16000)
	if err != nil {
		t.Fatal(err)
	}
	if diff := math.Abs(float64(len(got) - 16000)); diff > 4 {
		t.Fatalf("one second at 44.1 kHz became %d samples at 16 kHz, want about 16000", len(got))
	}
	if r := rms(got[500:]); r < 4000 || r > 7000 {
		t.Errorf("converted tone has RMS %.0f; the signal did not survive", r)
	}
}

func TestWAVRejectsNonsense(t *testing.T) {
	for name, data := range map[string][]byte{
		"empty":       {},
		"not a RIFF":  []byte("this is a text file, actually"),
		"header only": []byte("RIFF\x00\x00\x00\x00WAVE"),
	} {
		if _, err := ReadWAV16(bytes.NewReader(data), 16000); err == nil {
			t.Errorf("ReadWAV16 accepted %s", name)
		}
	}
}

// Chunks are walked rather than assumed at a fixed offset: real files carry LIST
// and fact chunks between fmt and data, and skipping to byte 44 reads metadata
// as audio.
func TestWAVSkipsUnknownChunks(t *testing.T) {
	var buf bytes.Buffer
	if err := WriteWAV16(&buf, tone(320, 16000, 440, 8000), 16000); err != nil {
		t.Fatal(err)
	}
	raw := buf.Bytes()

	// Splice a LIST chunk in between "fmt " and "data".
	at := bytes.Index(raw, []byte("data"))
	list := []byte("LIST\x04\x00\x00\x00INFO")
	spliced := append(append(append([]byte{}, raw[:at]...), list...), raw[at:]...)

	got, err := ReadWAV16(bytes.NewReader(spliced), 16000)
	if err != nil {
		t.Fatal(err)
	}
	if len(got) != 320 {
		t.Fatalf("got %d samples past a LIST chunk, want 320", len(got))
	}
}
