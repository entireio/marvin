// Package token mints and verifies the signed tokens this server issues.
//
// There are two kinds and they share a key and a construction: the cookie that
// says a person has signed in with GitHub, and the bearer token that says a
// robot is one of ours. Neither is stored anywhere. The server scales to zero
// and runs several instances at once, so a table of sessions or devices would
// need a database the deployment does not otherwise want; a signature answers
// the same question without one.
//
// The cost is that revocation means rotating the key, which signs everyone out
// and takes every robot with it. For a household with a robot in it that is the
// right trade. It stops being the right trade at roughly the point where this
// deployment has outgrown a handful of environment variables anyway.
package token

import (
	"crypto/hmac"
	"crypto/sha256"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"strings"
	"time"
)

// Sign returns "<payload>.<mac>" with both halves base64url-encoded.
func Sign(payload, key []byte) string {
	body := base64.RawURLEncoding.EncodeToString(payload)
	mac := hmac.New(sha256.New, key)
	mac.Write([]byte(body))
	return body + "." + base64.RawURLEncoding.EncodeToString(mac.Sum(nil))
}

// Unsign verifies the MAC and returns the payload it covers.
func Unsign(tok string, key []byte) ([]byte, error) {
	body, sig, ok := strings.Cut(tok, ".")
	if !ok {
		return nil, errors.New("malformed token")
	}
	mac := hmac.New(sha256.New, key)
	mac.Write([]byte(body))
	want := base64.RawURLEncoding.EncodeToString(mac.Sum(nil))
	// Constant-time: comparing with == would leak the signature one byte at a
	// time to anyone able to measure the response.
	if !hmac.Equal([]byte(sig), []byte(want)) {
		return nil, errors.New("bad signature")
	}
	return base64.RawURLEncoding.DecodeString(body)
}

// Device is the payload carried in a robot's bearer token.
type Device struct {
	DeviceID string `json:"did"`
	Issued   int64  `json:"iat"`
	Expires  int64  `json:"exp"`
}

// DeviceIDMaxLen bounds what will be accepted as a robot's name, so that a
// token cannot write an arbitrarily long string into every log line.
const DeviceIDMaxLen = 64

// MintDevice issues a token for a robot.
//
// The lifetime is long — a year by default — because renewing it means getting
// the robot back on a bench and talking to it over Bluetooth. A short-lived
// token would be more correct and would in practice mean a robot that stops
// working one morning for no reason its owner can see.
func MintDevice(deviceID string, key []byte, ttl time.Duration) (string, error) {
	deviceID, err := CleanDeviceID(deviceID)
	if err != nil {
		return "", err
	}
	now := time.Now()
	payload, err := json.Marshal(Device{
		DeviceID: deviceID,
		Issued:   now.Unix(),
		Expires:  now.Add(ttl).Unix(),
	})
	if err != nil {
		return "", err
	}
	return Sign(payload, key), nil
}

// DecodeDevice verifies a robot's token and returns what it claims.
func DecodeDevice(tok string, key []byte, now time.Time) (Device, error) {
	payload, err := Unsign(tok, key)
	if err != nil {
		return Device{}, err
	}
	var d Device
	if err := json.Unmarshal(payload, &d); err != nil {
		return Device{}, err
	}
	if _, err := CleanDeviceID(d.DeviceID); err != nil {
		return Device{}, err
	}
	if now.Unix() >= d.Expires {
		return Device{}, errors.New("device token expired")
	}
	return d, nil
}

// CleanDeviceID normalises and checks a robot's name.
//
// The character set is narrow because this string is logged on every connection
// and shown in the web controller. Anything that could carry an escape sequence
// into a terminal, or markup into a page, is rejected here rather than escaped
// at each of the places it comes out.
func CleanDeviceID(id string) (string, error) {
	id = strings.TrimSpace(id)
	if id == "" {
		return "", errors.New("device id is empty")
	}
	if len(id) > DeviceIDMaxLen {
		return "", fmt.Errorf("device id is %d characters, limit is %d", len(id), DeviceIDMaxLen)
	}
	for _, r := range id {
		switch {
		case r >= 'a' && r <= 'z', r >= 'A' && r <= 'Z', r >= '0' && r <= '9':
		case r == '-', r == '_', r == '.':
		default:
			return "", fmt.Errorf("device id contains %q; use letters, digits, dash, underscore or dot", r)
		}
	}
	return id, nil
}
