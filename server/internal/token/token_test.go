package token

import (
	"strings"
	"testing"
	"time"
)

var testKey = []byte("test-key-that-is-long-enough-to-pass-32")

// A device token is the only thing between a stranger and a live microphone in
// someone's home, so these test the ways one could be forged rather than the
// happy path alone.

func TestDeviceTokenRoundTrip(t *testing.T) {
	tok, err := MintDevice("marvin-1", testKey, 24*time.Hour)
	if err != nil {
		t.Fatal(err)
	}
	got, err := DecodeDevice(tok, testKey, time.Now())
	if err != nil {
		t.Fatalf("decoding a token we just minted: %v", err)
	}
	if got.DeviceID != "marvin-1" {
		t.Errorf("device id came back as %q, want marvin-1", got.DeviceID)
	}
	if got.Expires <= time.Now().Unix() {
		t.Error("a token minted for 24 hours is already expired")
	}
}

func TestDeviceTokenRejectsATamperedPayload(t *testing.T) {
	tok, err := MintDevice("marvin-1", testKey, time.Hour)
	if err != nil {
		t.Fatal(err)
	}
	// Swap the payload for one naming a different robot, keeping the signature.
	body, sig, _ := strings.Cut(tok, ".")
	forged := strings.Replace(body, body[:4], "AAAA", 1) + "." + sig

	if _, err := DecodeDevice(forged, testKey, time.Now()); err == nil {
		t.Fatal("a token with an altered payload was accepted")
	}
}

func TestDeviceTokenRejectsAnotherKey(t *testing.T) {
	tok, err := MintDevice("marvin-1", testKey, time.Hour)
	if err != nil {
		t.Fatal(err)
	}
	other := []byte("a-different-key-also-long-enough-32-chars")
	if _, err := DecodeDevice(tok, other, time.Now()); err == nil {
		t.Fatal("a token signed with one key verified under another")
	}
}

func TestDeviceTokenExpires(t *testing.T) {
	tok, err := MintDevice("marvin-1", testKey, time.Hour)
	if err != nil {
		t.Fatal(err)
	}
	later := time.Now().Add(2 * time.Hour)
	if _, err := DecodeDevice(tok, testKey, later); err == nil {
		t.Fatal("an expired token was accepted")
	}
}

// The device id is logged on every connection and rendered in the web
// controller, so it is checked where it enters rather than escaped at each of
// the places it leaves.
func TestDeviceIDRejectsUnsafeNames(t *testing.T) {
	for _, name := range []string{
		"",
		"   ",
		"has space",
		"esc\x1b[2Jape",
		"new\nline",
		"<script>alert(1)</script>",
		"../../etc/passwd",
		strings.Repeat("m", DeviceIDMaxLen+1),
	} {
		if _, err := CleanDeviceID(name); err == nil {
			t.Errorf("CleanDeviceID accepted %q", name)
		}
	}
}

func TestDeviceIDAcceptsOrdinaryNames(t *testing.T) {
	for _, name := range []string{"marvin", "marvin-1", "marvin_kitchen", "Marvin.2", "  marvin  "} {
		if _, err := CleanDeviceID(name); err != nil {
			t.Errorf("CleanDeviceID rejected %q: %v", name, err)
		}
	}
}
