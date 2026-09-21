#ifndef SETTINGS_H
#define SETTINGS_H

#include "config.h"

#ifdef MARVIN_VOICE

#include <Arduino.h>

// Everything the robot has to remember across a power cycle: which network to
// join, which backend to call, and the token that proves it is ours.
//
// All of it arrives over Bluetooth from the web controller, because the
// alternative is recompiling the firmware with someone's Wi-Fi password baked
// into it — which is how credentials end up committed to a public repository.
//
// Stored in NVS. The token is the sensitive one: it is a bearer credential for
// a live microphone, so it is never echoed back out, and the status command
// reports only whether one is present.

class Settings {
public:
    // Open the namespace and load what is there. Call once in setup().
    void begin();

    bool haveWiFi()  const { return _ssid.length() > 0; }
    bool haveCloud() const { return _url.length() > 0 && _token.length() > 0; }

    const String& ssid()     const { return _ssid; }
    const String& password() const { return _password; }
    const String& url()      const { return _url; }
    const String& token()    const { return _token; }
    const String& deviceId() const { return _deviceId; }

    void setWiFi(const String& ssid, const String& password);
    void setCloud(const String& url, const String& token);
    void setDeviceId(const String& id);

    // Forget everything. For handing the robot to someone else, or for getting
    // out of a half-finished setup.
    void clear();

    // A human-readable summary with no secrets in it.
    String summary() const;

private:
    String _ssid;
    String _password;
    String _url;
    String _token;
    String _deviceId;
};

#endif // MARVIN_VOICE
#endif // SETTINGS_H
