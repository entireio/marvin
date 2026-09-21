// Package device is the robot's end of the backend: the WebSocket endpoint
// Marvin connects to, and the loop that turns what it hears into a
// conversation with an AI service.
package device

import (
	"fmt"
	"sync"
	"time"

	"github.com/spedemon/marvin/server/harness"
	"github.com/spedemon/marvin/server/protocol"
)

// sender is the half of a connection the hub needs: somewhere to put a message
// bound for a robot. Satisfied by *conn.
type sender interface {
	Control(protocol.Message) error
}

// State is what the backend knows about one robot.
type State struct {
	ID          string    `json:"id"`
	Firmware    string    `json:"firmware,omitempty"`
	ConnectedAt time.Time `json:"connected_at"`
	LastSeen    time.Time `json:"last_seen"`
	RSSI        int       `json:"rssi,omitempty"`
	BatteryMV   int       `json:"battery_mv,omitempty"`
	Provider    string    `json:"provider,omitempty"`
	InTurn      bool      `json:"in_turn"`
}

// Update is one thing the web controller should be told about.
type Update struct {
	Event       string               `json:"event"` // connected, disconnected, state, observation
	DeviceID    string               `json:"device_id"`
	State       *State               `json:"state,omitempty"`
	Observation *harness.Observation `json:"observation,omitempty"`
}

// Hub tracks connected robots and fans updates out to whoever is watching from
// the web controller.
//
// It is memory-only and per-instance on purpose. The server scales to several
// instances and to zero, so anything durable would have to live in a database
// the deployment does not otherwise need. What is lost on a restart is a live
// view, which reappears the moment the robot reconnects.
type Hub struct {
	mu      sync.RWMutex
	devices map[string]*State
	conns   map[string]sender
	subs    map[chan Update]struct{}
}

// NewHub returns an empty hub.
func NewHub() *Hub {
	return &Hub{
		devices: make(map[string]*State),
		conns:   make(map[string]sender),
		subs:    make(map[chan Update]struct{}),
	}
}

// Subscribe returns a channel of updates and a function to stop listening.
//
// The channel is buffered and lossy: a subscriber that stops reading gets
// dropped updates rather than stalling a robot's audio path. A live view is
// worth less than a conversation.
func (h *Hub) Subscribe() (<-chan Update, func()) {
	ch := make(chan Update, 64)
	h.mu.Lock()
	h.subs[ch] = struct{}{}
	h.mu.Unlock()

	var once sync.Once
	return ch, func() {
		once.Do(func() {
			h.mu.Lock()
			delete(h.subs, ch)
			h.mu.Unlock()
			close(ch)
		})
	}
}

// Devices returns a snapshot of every connected robot.
func (h *Hub) Devices() []State {
	h.mu.RLock()
	defer h.mu.RUnlock()
	out := make([]State, 0, len(h.devices))
	for _, s := range h.devices {
		out = append(out, *s)
	}
	return out
}

func (h *Hub) connected(id, firmware string, c sender) {
	now := time.Now()
	s := &State{ID: id, Firmware: firmware, ConnectedAt: now, LastSeen: now}

	h.mu.Lock()
	h.devices[id] = s
	h.conns[id] = c
	h.mu.Unlock()

	h.broadcast(Update{Event: "connected", DeviceID: id, State: s})
}

func (h *Hub) disconnected(id string) {
	h.mu.Lock()
	delete(h.devices, id)
	delete(h.conns, id)
	h.mu.Unlock()

	h.broadcast(Update{Event: "disconnected", DeviceID: id})
}

// update applies a change to a device's state and publishes the result.
func (h *Hub) update(id string, fn func(*State)) {
	h.mu.Lock()
	s, ok := h.devices[id]
	if ok {
		fn(s)
		s.LastSeen = time.Now()
	}
	// Copy under the lock: subscribers read this without one, and the device's
	// own goroutine will keep mutating the original.
	var snapshot State
	if ok {
		snapshot = *s
	}
	h.mu.Unlock()

	if ok {
		h.broadcast(Update{Event: "state", DeviceID: id, State: &snapshot})
	}
}

// Send delivers a control message to a connected robot.
//
// This is how the web controller reaches a robot it is not standing next to:
// the button that starts a conversation on a board with no wake word comes
// through here rather than over Bluetooth, so it works from anywhere the
// controller does.
func (h *Hub) Send(id string, m protocol.Message) error {
	h.mu.RLock()
	c, ok := h.conns[id]
	h.mu.RUnlock()

	if !ok {
		return fmt.Errorf("device %q is not connected", id)
	}
	// Called outside the lock: Control writes to a socket, and a robot whose
	// Wi-Fi has dropped would otherwise hold the hub shut for the write
	// timeout, freezing every other robot's state updates behind it.
	return c.Control(m)
}

func (h *Hub) observe(id string, o harness.Observation) {
	h.broadcast(Update{Event: "observation", DeviceID: id, Observation: &o})
}

func (h *Hub) broadcast(u Update) {
	h.mu.RLock()
	defer h.mu.RUnlock()
	for ch := range h.subs {
		select {
		case ch <- u:
		default: // slow subscriber; drop rather than block the robot
		}
	}
}
