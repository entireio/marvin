#include "settings.h"

#ifdef MARVIN_VOICE

#include <Preferences.h>

// One namespace, short keys: NVS keys are limited to fifteen characters.
static const char* NVS_NAMESPACE = "marvin";

static const char* KEY_SSID     = "ssid";
static const char* KEY_PASSWORD = "pass";
static const char* KEY_URL      = "url";
static const char* KEY_TOKEN    = "token";
static const char* KEY_DEVICE   = "device";

static Preferences prefs;

// Reading a key that is not there logs an error inside Preferences, and on a
// robot that has never been set up none of them are there. Five error lines for
// the most ordinary state a new board can be in is misleading, so ask first.
static String readSetting(const char* key, const char* fallback = "") {
    if (!prefs.isKey(key)) return String(fallback);
    return prefs.getString(key, fallback);
}

void Settings::begin() {
    prefs.begin(NVS_NAMESPACE, /*readOnly=*/false);
    _ssid     = readSetting(KEY_SSID);
    _password = readSetting(KEY_PASSWORD);
    _url      = readSetting(KEY_URL);
    _token    = readSetting(KEY_TOKEN);
    _deviceId = readSetting(KEY_DEVICE, BLE_DEVICE_NAME);
}

void Settings::setWiFi(const String& ssid, const String& password) {
    _ssid = ssid;
    _password = password;
    prefs.putString(KEY_SSID, ssid);
    prefs.putString(KEY_PASSWORD, password);
}

void Settings::setCloud(const String& url, const String& token) {
    if (url.length() > 0) {
        _url = url;
        prefs.putString(KEY_URL, url);
    }
    if (token.length() > 0) {
        _token = token;
        prefs.putString(KEY_TOKEN, token);
    }
}

void Settings::setDeviceId(const String& id) {
    _deviceId = id;
    prefs.putString(KEY_DEVICE, id);
}

void Settings::clear() {
    prefs.clear();
    _ssid = _password = _url = _token = "";
    _deviceId = BLE_DEVICE_NAME;
}

String Settings::summary() const {
    String s = "device: " + _deviceId + "\r\n";
    // "ssid", not "wifi": NetWiFi::summary() prints the live connection on a
    // line of its own, and two lines both labelled wifi read as a contradiction
    // when one says a name and the other says not connected.
    s += "ssid:   " + (_ssid.length() ? _ssid : String("(not set)")) + "\r\n";
    s += "url:    " + (_url.length() ? _url : String("(not set)")) + "\r\n";
    // Never the token itself. It is a bearer credential for a live microphone,
    // and this string goes to a Bluetooth terminal and a serial console.
    s += "token:  " + String(_token.length() ? "set" : "(not set)") + "\r\n";
    return s;
}

#endif // MARVIN_VOICE
