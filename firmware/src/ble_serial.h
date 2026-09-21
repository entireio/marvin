#ifndef BLE_SERIAL_H
#define BLE_SERIAL_H

#include <NimBLEDevice.h>

// Nordic UART Service UUIDs (de-facto standard for serial-over-BLE)
#define NUS_SERVICE_UUID "6E400001-B5A3-F393-E0A9-E50E24DCCA9E"
#define NUS_RX_UUID      "6E400002-B5A3-F393-E0A9-E50E24DCCA9E"  // Client writes here
#define NUS_TX_UUID      "6E400003-B5A3-F393-E0A9-E50E24DCCA9E"  // Server notifies here

// Provisioning — Marvin's own, not part of the Nordic standard.
//
// A second characteristic rather than more commands on the first, because this
// one requires an encrypted link. Wi-Fi passwords and the backend token go
// through here; anyone with a radio can read an unencrypted BLE write from the
// next room. Writing to a characteristic marked WRITE_ENC is also what prompts
// the browser and the operating system to pair in the first place, so the
// requirement is what creates the encryption rather than merely checking for it.
#define NUS_PROVISION_UUID "6E400004-B5A3-F393-E0A9-E50E24DCCA9E"

// Callback type: called when a complete line is received over BLE
typedef void (*BleCommandCallback)(const String& command);

class BleSerial {
public:
    // Initialise BLE stack, create GATT server, start advertising.
    //
    // onProvision may be null on builds without voice, in which case the
    // provisioning characteristic is not created at all.
    void begin(const char* deviceName, BleCommandCallback callback,
               BleCommandCallback onProvision = nullptr);

    // Send a string to the connected client via TX notifications.
    // Automatically fragments into BLE-safe chunks.
    void send(const char* data);
    void send(const String& data);

    // True when a GATT client is connected.
    bool isConnected() const;

private:
    NimBLECharacteristic* _txChar = nullptr;
    NimBLEServer*         _server = nullptr;
    bool                  _connected = false;
    BleCommandCallback    _callback  = nullptr;
    BleCommandCallback    _provision = nullptr;
    String                _rxBuffer;
    String                _provisionBuffer;

    // NimBLE callback classes need access to private members
    friend class BleServerCallbacks;
    friend class BleRxCallbacks;
    friend class BleProvisionCallbacks;
};

#endif // BLE_SERIAL_H
