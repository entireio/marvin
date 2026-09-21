#ifndef NET_WIFI_H
#define NET_WIFI_H

#include "config.h"

#ifdef MARVIN_VOICE

#include <Arduino.h>
#include <WiFi.h>   // arduino_event_id_t and friends, used in the callback below
#include "settings.h"

// Joining the house network, rejoining it when it goes away, and saying out
// loud what is happening at each step.
//
// The saying-out-loud is half the point. Provisioning fails for dull reasons —
// a typo in the password, the wrong band, a network that is simply out of range
// — and a robot that reports only "not connected" leaves you guessing which.
// Every state change here becomes a line on the serial console and, through the
// same path, in the web controller.
//
// A state machine polled from loop() rather than a blocking connect: Marvin
// still has to answer its Bluetooth terminal and hold its head up while the
// router is rebooting.

// Lines are emitted with a prefix the controller can pick out of the terminal
// stream: "[WIFI] ..." for state, "[NET] ..." for one scan result.
typedef void (*NetEventHandler)(const String& line);

class NetWiFi {
public:
    enum class Phase { Idle, Scanning, Connecting, Connected, Failed };

    void begin(Settings& settings, NetEventHandler onEvent);

    // Call from loop(). Drives connection attempts, backoff and scan results.
    void update();

    // Start an asynchronous scan. Results arrive as "[NET]" lines followed by
    // "[WIFI] scan_done". Refused while a connection attempt is in flight,
    // because scanning takes the radio away from it.
    bool startScan();

    // Connect now with whatever credentials are stored, clearing any backoff.
    // Someone who has just typed a password is entitled to a fast answer about
    // whether it was the right one.
    void reconnectNow();

    bool isConnected() const;
    Phase phase() const { return _phase; }
    int  rssi() const;
    String ip() const;

    // One line for the status command.
    String summary() const;

    // The most recent failure, as one of the short reasons used on the wire:
    // "auth", "notfound", "timeout", "other". Empty when there has been none.
    const String& lastError() const { return _lastError; }

private:
    Settings*       _settings = nullptr;
    NetEventHandler _onEvent = nullptr;

    Phase         _phase = Phase::Idle;
    unsigned long _phaseStartedMs = 0;
    unsigned long _nextAttemptMs = 0;
    uint32_t      _backoffMs = 0;
    uint8_t       _failures = 0;
    String        _lastError;
    bool          _scanRequested = false;

    // Written by the Wi-Fi event task, read by update() on the main loop. Only
    // the latest matters, so a flag and a code are enough and there is no queue
    // to get wrong.
    volatile bool     _eventPending = false;
    volatile uint8_t  _eventKind = 0;   // 1 got-ip, 2 disconnected
    volatile uint8_t  _eventReason = 0;

    void _startAttempt();
    void _noteFailure(const String& reason);
    void _emit(const String& line);
    void _pollScan();
    void _handleEvent();

    static void _onWiFiEvent(arduino_event_id_t event, arduino_event_info_t info);
};

#endif // MARVIN_VOICE
#endif // NET_WIFI_H
