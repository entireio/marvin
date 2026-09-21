#include "net_wifi.h"

#ifdef MARVIN_VOICE

#include <WiFi.h>

// How long to let one attempt run before calling it failed. Long enough for a
// slow DHCP lease, short enough that a wrong password is reported while the
// person who typed it is still standing there.
static const unsigned long CONNECT_TIMEOUT_MS = 15000;

// Backoff between attempts. Capped at half a minute: a robot that has been
// waiting hours for a router to come back should still notice within thirty
// seconds of it doing so.
static const uint32_t BACKOFF_MIN_MS = 2000;
static const uint32_t BACKOFF_MAX_MS = 30000;

// Most networks a scan will report. Enough for a crowded flat; past this the
// list is unreadable anyway and the notifications cost Bluetooth airtime.
static const int MAX_SCAN_RESULTS = 24;

// The Wi-Fi event callback is a plain function, so it needs a module-level
// instance the same way the BLE and WebSocket callbacks do.
static NetWiFi* _instance = nullptr;

void NetWiFi::begin(Settings& settings, NetEventHandler onEvent) {
    _settings = &settings;
    _onEvent = onEvent;
    _instance = this;

    WiFi.mode(WIFI_STA);
    // Our own state machine does the reconnecting, so that failures are
    // counted, explained and reported rather than retried silently forever.
    WiFi.setAutoReconnect(false);
    // Modem sleep is what makes an always-connected robot cost tens of
    // milliamps instead of over a hundred. It adds latency to the first packet
    // after an idle spell, which is invisible next to the time it takes a
    // person to finish a sentence.
    WiFi.setSleep(true);
    WiFi.onEvent(_onWiFiEvent);

    if (_settings->haveWiFi()) {
        _startAttempt();
    } else {
        _emit("[WIFI] unconfigured");
    }
}

// Runs on the Wi-Fi event task. It records what happened and gets out; the
// reporting, which touches Bluetooth, happens on the main loop.
void NetWiFi::_onWiFiEvent(arduino_event_id_t event, arduino_event_info_t info) {
    if (!_instance) return;
    switch (event) {
    case ARDUINO_EVENT_WIFI_STA_GOT_IP:
        _instance->_eventKind = 1;
        _instance->_eventPending = true;
        break;
    case ARDUINO_EVENT_WIFI_STA_DISCONNECTED:
        _instance->_eventKind = 2;
        _instance->_eventReason = info.wifi_sta_disconnected.reason;
        _instance->_eventPending = true;
        break;
    default:
        break;
    }
}

// Turn the driver's numeric reason into something a person can act on. The
// distinction that matters is "your password is wrong" versus "that network is
// not here" — everything else is noise to whoever is standing at the robot.
static String reasonText(uint8_t reason) {
    switch (reason) {
    case WIFI_REASON_NO_AP_FOUND:
        return "notfound";
    case WIFI_REASON_AUTH_FAIL:
    case WIFI_REASON_AUTH_EXPIRE:
    case WIFI_REASON_4WAY_HANDSHAKE_TIMEOUT:
    case WIFI_REASON_HANDSHAKE_TIMEOUT:
    case WIFI_REASON_CONNECTION_FAIL:
        return "auth";
    default:
        return "other";
    }
}

void NetWiFi::_handleEvent() {
    if (!_eventPending) return;
    _eventPending = false;

    const uint8_t kind = _eventKind;
    const uint8_t reason = _eventReason;

    if (kind == 1) { // got an address
        _phase = Phase::Connected;
        _failures = 0;
        _backoffMs = 0;
        _lastError = "";
        _emit("[WIFI] connected " + WiFi.localIP().toString() + " " + String(WiFi.RSSI()));
        return;
    }

    if (kind == 2) { // dropped, or an attempt failed
        if (_phase == Phase::Connected) {
            _phase = Phase::Idle;
            _nextAttemptMs = millis(); // try again at once; back off only on failure
            _emit("[WIFI] disconnected");
        } else if (_phase == Phase::Connecting) {
            _noteFailure(reasonText(reason));
        }
    }
}

void NetWiFi::update() {
    _handleEvent();
    _pollScan();

    if (!_settings || !_settings->haveWiFi()) return;
    if (_phase == Phase::Scanning) return; // the radio is busy

    switch (_phase) {
    case Phase::Idle:
    case Phase::Failed:
        if (millis() >= _nextAttemptMs) _startAttempt();
        break;

    case Phase::Connecting:
        // The disconnect event usually reports the failure first and with a
        // reason. This is the backstop for an attempt that simply stalls.
        if (millis() - _phaseStartedMs > CONNECT_TIMEOUT_MS) {
            _noteFailure("timeout");
        }
        break;

    default:
        break;
    }
}

void NetWiFi::_startAttempt() {
    _phase = Phase::Connecting;
    _phaseStartedMs = millis();
    _emit("[WIFI] connecting " + _settings->ssid());
    WiFi.disconnect();
    WiFi.begin(_settings->ssid().c_str(), _settings->password().c_str());
}

void NetWiFi::_noteFailure(const String& reason) {
    _failures++;
    _lastError = reason;
    _backoffMs = _backoffMs ? min(_backoffMs * 2, BACKOFF_MAX_MS) : BACKOFF_MIN_MS;
    _phase = Phase::Failed;
    _nextAttemptMs = millis() + _backoffMs;

    _emit("[WIFI] failed " + reason);
    _emit("[WIFI] retry " + String(_backoffMs));
}

void NetWiFi::reconnectNow() {
    _failures = 0;
    _backoffMs = 0;
    _lastError = "";
    _nextAttemptMs = 0;
    if (_settings && _settings->haveWiFi()) _startAttempt();
}

// --- scanning --------------------------------------------------------------

bool NetWiFi::startScan() {
    if (_phase == Phase::Scanning) return true; // already looking
    if (_phase == Phase::Connecting) {
        // Scanning takes the radio off the attempt in progress, which would
        // fail it for a reason that has nothing to do with the credentials.
        _emit("[WIFI] scan_busy");
        return false;
    }

    WiFi.scanDelete();
    // Asynchronous, and including hidden networks so the count is honest even
    // though an unnamed one is no use in a list.
    if (WiFi.scanNetworks(/*async=*/true, /*show_hidden=*/true) == WIFI_SCAN_FAILED) {
        _emit("[WIFI] scan_failed");
        return false;
    }
    _scanRequested = true;
    _phase = Phase::Scanning;
    _phaseStartedMs = millis();
    _emit("[WIFI] scanning");
    return true;
}

void NetWiFi::_pollScan() {
    if (!_scanRequested) return;

    int found = WiFi.scanComplete();
    if (found == WIFI_SCAN_RUNNING) {
        // A scan that never finishes would strand the state machine here and
        // the robot would stop trying to connect.
        if (millis() - _phaseStartedMs > 20000) {
            WiFi.scanDelete();
            _scanRequested = false;
            _phase = Phase::Idle;
            _emit("[WIFI] scan_failed");
        }
        return;
    }
    _scanRequested = false;

    if (found < 0) {
        _phase = Phase::Idle;
        _emit("[WIFI] scan_failed");
        return;
    }

    int reported = 0;
    for (int i = 0; i < found && reported < MAX_SCAN_RESULTS; i++) {
        String ssid = WiFi.SSID(i);
        if (ssid.length() == 0) continue; // hidden; nothing to put in a list

        // The separator is '|', so an SSID containing one would split into two
        // fields at the other end. Rare, but silently mangling somebody's
        // network name is worse than skipping it.
        if (ssid.indexOf('|') >= 0) continue;

        const bool open = WiFi.encryptionType(i) == WIFI_AUTH_OPEN;
        _emit("[NET] " + String(WiFi.RSSI(i)) + "|" + (open ? "open" : "secure") + "|" + ssid);
        reported++;
    }
    WiFi.scanDelete();

    _emit("[WIFI] scan_done " + String(reported));

    // Scanning left the radio idle. Pick the connection attempt back up rather
    // than waiting for the next backoff tick.
    _phase = isConnected() ? Phase::Connected : Phase::Idle;
    _nextAttemptMs = millis();
}

// --- reporting -------------------------------------------------------------

void NetWiFi::_emit(const String& line) {
    if (_onEvent) _onEvent(line);
}

bool NetWiFi::isConnected() const { return WiFi.status() == WL_CONNECTED; }
int  NetWiFi::rssi() const        { return isConnected() ? WiFi.RSSI() : 0; }
String NetWiFi::ip() const        { return isConnected() ? WiFi.localIP().toString() : String("0.0.0.0"); }

String NetWiFi::summary() const {
    if (!_settings || !_settings->haveWiFi()) return "wifi:   no network configured\r\n";
    if (isConnected()) {
        return "wifi:   connected to " + _settings->ssid() + " as " + ip() +
               " (" + String(rssi()) + " dBm)\r\n";
    }
    String s = "wifi:   not connected";
    if (_lastError.length()) s += ", last error: " + _lastError;
    if (_failures) s += " (" + String(_failures) + " failed attempts)";
    return s + "\r\n";
}

#endif // MARVIN_VOICE
