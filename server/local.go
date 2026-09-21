package main

import (
	"log"
	"net"
	"net/http"
	"strings"
)

// Running the whole system on a bench, with the robot on the same Wi-Fi.
//
// Without this, trying anything at all means registering a GitHub OAuth app and
// deploying to a cloud — which is a lot of ceremony to find out whether a
// microphone is wired the right way round. Local mode drops the sign-in so the
// server can be started with one command.
//
// It is off unless MARVIN_LOCAL=true is set explicitly. Neither deploy script
// sets it, so it cannot travel to a cloud by accident, and the guard below
// means that even if one did, the result would not be an open door.

// localOnly refuses requests that did not come from this machine or this
// network.
//
// The sign-in is the thing keeping strangers out, and local mode turns it off.
// So something else has to hold the line: a request whose Host is a public name
// or a public address is not coming from the bench this mode is for. A Host
// header can be forged by someone already inside the network, which is the
// point — this defends against the port forward somebody left open, not against
// the person sitting next to the robot.
func localOnly(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		host := r.Host
		if h, _, err := net.SplitHostPort(host); err == nil {
			host = h
		}
		if !isLocalHost(host) {
			log.Printf("local mode: refused a request for host %q", host)
			http.Error(w, "this server is running in local mode and serves only "+
				"localhost and private addresses", http.StatusForbidden)
			return
		}
		next.ServeHTTP(w, r)
	})
}

func isLocalHost(host string) bool {
	host = strings.TrimSuffix(strings.ToLower(host), ".")
	if host == "localhost" || strings.HasSuffix(host, ".local") {
		return true
	}
	ip := net.ParseIP(strings.Trim(host, "[]"))
	if ip == nil {
		// A name that is not localhost. Could resolve anywhere; do not serve it.
		return false
	}
	return ip.IsLoopback() || ip.IsPrivate() || ip.IsLinkLocalUnicast()
}

// lanAddress is the address this machine can be reached on from the robot.
//
// Needed because the controller has to be opened at localhost — Web Bluetooth
// refuses to run anywhere else without HTTPS — while the robot has to be given
// something it can actually route to. Without this the setup form would
// cheerfully write "localhost" into the robot, and the only symptom would be a
// connection that never succeeds.
func lanAddress() string {
	// Asking the routing table which source address it would use to reach the
	// internet. No packets are sent — a UDP "connection" is purely local — and
	// it picks the right interface on a machine with several, which enumerating
	// them does not.
	if conn, err := net.Dial("udp4", "8.8.8.8:53"); err == nil {
		defer conn.Close()
		if addr, ok := conn.LocalAddr().(*net.UDPAddr); ok && addr.IP.IsPrivate() {
			return addr.IP.String()
		}
	}

	// No route to the internet — an isolated bench network is a normal way to
	// work. Fall back to the first private address on any interface that is up.
	ifaces, err := net.Interfaces()
	if err != nil {
		return ""
	}
	for _, iface := range ifaces {
		if iface.Flags&net.FlagUp == 0 || iface.Flags&net.FlagLoopback != 0 {
			continue
		}
		addrs, err := iface.Addrs()
		if err != nil {
			continue
		}
		for _, a := range addrs {
			if ipnet, ok := a.(*net.IPNet); ok {
				if ip4 := ipnet.IP.To4(); ip4 != nil && ip4.IsPrivate() {
					return ip4.String()
				}
			}
		}
	}
	return ""
}

// deviceURL is the address to write into a robot: the one the controller should
// offer, rather than the one the browser happens to be using.
func (a *app) deviceURL(r *http.Request) string {
	scheme := "wss"
	if !isHTTPS(r) {
		scheme = "ws"
	}

	host := r.Host
	// On a bench the browser is at localhost and the robot is not. Swap in an
	// address the robot can route to, keeping the port.
	if h, port, err := net.SplitHostPort(host); err == nil {
		if ip := net.ParseIP(h); (ip != nil && ip.IsLoopback()) || strings.EqualFold(h, "localhost") {
			if lan := lanAddress(); lan != "" {
				host = net.JoinHostPort(lan, port)
			}
		}
	}
	return scheme + "://" + host + "/v1/device"
}

// warnAboutLocalMode says out loud what has been turned off. Someone who set
// this by accident should find out at startup, not from a stranger.
func warnAboutLocalMode(port string) {
	lan := lanAddress()
	if lan == "" {
		lan = "this machine"
	}
	log.Print("┌─────────────────────────────────────────────────────────────┐")
	log.Print("│ LOCAL MODE — the GitHub sign-in is OFF                       │")
	log.Print("│ Anyone on this network can reach the controller and drive    │")
	log.Print("│ the robot. Only requests for localhost or a private address  │")
	log.Print("│ are served at all. Never use this on a public host.          │")
	log.Print("└─────────────────────────────────────────────────────────────┘")
	log.Printf("controller:  http://localhost:%s/app/", port)
	log.Printf("             (localhost, not the LAN address — Web Bluetooth")
	log.Printf("              refuses to run on anything else without HTTPS)")
	log.Printf("robot:       ws://%s:%s/v1/device", lan, port)
}
