// Command fakedevice pretends to be Marvin.
//
// It connects to the backend exactly as the robot does, streams a WAV file as
// though it were the microphone, and writes what comes back to another WAV. So
// the entire server — device link, provider, harness, resampling — can be
// exercised from a laptop with no robot, no microphone and no soldering:
//
//	go run ./cmd/fakedevice -key "$SESSION_SECRET" -in question.wav -out answer.wav
//
// That loop is the difference between debugging the backend in seconds and
// debugging it by walking to a shelf and power-cycling something.
package main

import (
	"context"
	"flag"
	"fmt"
	"log"
	"net/http"
	"os"
	"os/signal"
	"strings"
	"time"

	"github.com/coder/websocket"

	"github.com/spedemon/marvin/server/audio"
	"github.com/spedemon/marvin/server/internal/token"
	"github.com/spedemon/marvin/server/protocol"
)

func main() {
	var (
		url      = flag.String("url", "ws://localhost:8080/v1/device", "backend device endpoint")
		bearer   = flag.String("token", "", "device token; minted from -key when empty")
		key      = flag.String("key", os.Getenv("SESSION_SECRET"), "server SESSION_SECRET, to mint a token")
		deviceID = flag.String("device", "fakedevice", "device id to claim")
		inPath   = flag.String("in", "", "WAV file to send as microphone audio (any rate, mono or stereo)")
		outPath  = flag.String("out", "reply.wav", "WAV file to write the reply to")
		realtime = flag.Bool("realtime", true, "pace uplink at 20 ms per frame, as the robot does")
		wait     = flag.Bool("wait", false, "do not start a turn; wait for the server to ask (W1), as a robot with no wake word does")
	)
	flag.Parse()

	if err := run(*url, *bearer, *key, *deviceID, *inPath, *outPath, *realtime, *wait); err != nil {
		log.Fatal(err)
	}
}

func run(url, bearer, key, deviceID, inPath, outPath string, realtime, wait bool) error {
	if bearer == "" {
		if key == "" {
			return fmt.Errorf("need -token, or -key (or SESSION_SECRET) to mint one")
		}
		var err error
		if bearer, err = token.MintDevice(deviceID, []byte(key), time.Hour); err != nil {
			return err
		}
	}

	var uplink []int16
	if inPath != "" {
		f, err := os.Open(inPath)
		if err != nil {
			return err
		}
		defer f.Close()
		if uplink, err = audio.ReadWAV16(f, protocol.DeviceSampleRate); err != nil {
			return fmt.Errorf("reading %s: %w", inPath, err)
		}
		log.Printf("loaded %s: %.1f seconds at %d Hz",
			inPath, float64(len(uplink))/protocol.DeviceSampleRate, protocol.DeviceSampleRate)
	}

	// Ctrl-C should hang up cleanly, the way a robot losing power does not.
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt)
	defer stop()

	ws, _, err := websocket.Dial(ctx, url, &websocket.DialOptions{
		HTTPHeader: http.Header{"Authorization": {"Bearer " + bearer}},
	})
	if err != nil {
		return fmt.Errorf("dialling %s: %w", url, err)
	}
	defer ws.Close(websocket.StatusNormalClosure, "")
	ws.SetReadLimit(8 << 10)

	if err := writeMessage(ctx, ws, protocol.Message{
		Type:       protocol.MsgHello,
		DeviceID:   deviceID,
		Firmware:   "fakedevice",
		Codec:      protocol.CodecPCM16,
		SampleRate: protocol.DeviceSampleRate,
	}); err != nil {
		return err
	}

	// start carries the moment the turn begins: either straight away, or when
	// the server asks — which is how a board with no wake word behaves.
	start := make(chan struct{}, 1)
	replies := make(chan []int16, 1)
	go func() { replies <- readLoop(ctx, ws, start) }()

	if wait {
		log.Print("waiting for the server to say W1 (press Start listening in the controller)")
		select {
		case <-start:
		case <-ctx.Done():
			return nil
		}
	} else {
		// Let the server accept the session before claiming a wake word.
		time.Sleep(300 * time.Millisecond)
		log.Print(`saying "Hey Marvin"`)
	}

	// Either way the robot announces the start of a turn the same way. The
	// firmware does this from startListening(), whether a wake word or a button
	// got it there, and it is what makes the backend open a provider session —
	// so a simulator that skipped it would exercise a path nothing real takes.
	if err := writeMessage(ctx, ws, protocol.Message{Type: protocol.MsgWake}); err != nil {
		return err
	}

	if len(uplink) > 0 {
		if err := streamAudio(ctx, ws, uplink, realtime); err != nil {
			return err
		}
		log.Print("microphone audio sent; ending the turn")
		if err := writeMessage(ctx, ws, protocol.Message{Type: protocol.MsgTurnEnd}); err != nil {
			return err
		}
	}

	samples := <-replies
	if len(samples) == 0 {
		log.Print("no audio came back")
		return nil
	}

	f, err := os.Create(outPath)
	if err != nil {
		return err
	}
	defer f.Close()
	if err := audio.WriteWAV16(f, samples, protocol.DeviceSampleRate); err != nil {
		return err
	}
	log.Printf("wrote %s: %.1f seconds",
		outPath, float64(len(samples))/protocol.DeviceSampleRate)
	return nil
}

// streamAudio sends the file 20 ms at a time, pacing it the way the robot does.
//
// The pacing matters: the services use silence to decide the speaker has
// finished, and a file delivered as fast as the socket allows arrives as one
// undifferentiated burst that their turn detection cannot read.
func streamAudio(ctx context.Context, ws *websocket.Conn, samples []int16, realtime bool) error {
	var (
		seq   uint16
		frame []byte
		buf   []byte
		tick  = time.NewTicker(protocol.DeviceFrameMillis * time.Millisecond)
	)
	defer tick.Stop()

	for off := 0; off < len(samples); off += protocol.DeviceFrameSamples {
		end := min(off+protocol.DeviceFrameSamples, len(samples))
		chunk := samples[off:end]

		buf = audio.AppendBytes(buf[:0], chunk)
		// Pad the last short frame: the robot never sends a partial one.
		for len(buf) < protocol.DeviceFrameBytes {
			buf = append(buf, 0)
		}

		frame = protocol.AppendFrame(frame[:0], protocol.Frame{
			Type: protocol.FrameAudioUp, Seq: seq, Payload: buf,
		})
		seq++

		if err := ws.Write(ctx, websocket.MessageBinary, frame); err != nil {
			return err
		}
		if realtime {
			select {
			case <-tick.C:
			case <-ctx.Done():
				return ctx.Err()
			}
		}
	}
	return nil
}

// readLoop collects the reply and narrates the control messages, returning when
// the turn ends or the connection does.
func readLoop(ctx context.Context, ws *websocket.Conn, start chan<- struct{}) []int16 {
	var reply []int16
	for {
		typ, data, err := ws.Read(ctx)
		if err != nil {
			return reply
		}
		switch typ {
		case websocket.MessageBinary:
			f, err := protocol.Decode(data)
			if err != nil {
				log.Printf("bad frame: %v", err)
				continue
			}
			if f.Type == protocol.FrameAudioDown {
				reply = audio.AppendInt16(reply, f.Payload)
			}
		case websocket.MessageText:
			msg, err := protocol.DecodeMessage(data)
			if err != nil {
				log.Printf("undecodable control message: %v", err)
				continue
			}
			log.Printf("<- %s%s", msg.Type, detail(msg))
			switch msg.Type {
			case protocol.MsgAct:
				// What the firmware does with these: hand them to the same
				// command parser the serial console uses. W1 starts listening.
				if msg.Cmd == "W1" {
					select {
					case start <- struct{}{}:
					default:
					}
				}
			case protocol.MsgSpeakEnd:
				return reply
			case protocol.MsgError:
				return reply
			}
		}
	}
}

func detail(m protocol.Message) string {
	var parts []string
	if m.Provider != "" {
		parts = append(parts, "provider="+m.Provider)
	}
	if m.SessionID != "" {
		parts = append(parts, "session="+m.SessionID)
	}
	if m.Gesture != "" {
		parts = append(parts, "gesture="+m.Gesture)
	}
	if m.Cmd != "" {
		parts = append(parts, "cmd="+m.Cmd)
	}
	if m.Message != "" {
		parts = append(parts, m.Message)
	}
	if len(parts) == 0 {
		return ""
	}
	return " (" + strings.Join(parts, ", ") + ")"
}

func writeMessage(ctx context.Context, ws *websocket.Conn, m protocol.Message) error {
	b, err := protocol.EncodeMessage(m)
	if err != nil {
		return err
	}
	log.Printf("-> %s", m.Type)
	return ws.Write(ctx, websocket.MessageText, b)
}
