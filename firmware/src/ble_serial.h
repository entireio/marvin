#ifndef BLE_SERIAL_H
#define BLE_SERIAL_H

#include <NimBLEDevice.h>

// Nordic UART Service UUIDs (de-facto standard for serial-over-BLE)
#define NUS_SERVICE_UUID "6E400001-B5A3-F393-E0A9-E50E24DCCA9E"
#define NUS_RX_UUID      "6E400002-B5A3-F393-E0A9-E50E24DCCA9E"  // Client writes here
#define NUS_TX_UUID      "6E400003-B5A3-F393-E0A9-E50E24DCCA9E"  // Server notifies here

// Callback type: called when a complete line is received over BLE
typedef void (*BleCommandCallback)(const String& command);

class BleSerial {
public:
    // Initialise BLE stack, create GATT server, start advertising.
    void begin(const char* deviceName, BleCommandCallback callback);

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
    String                _rxBuffer;

    // NimBLE callback classes need access to private members
    friend class BleServerCallbacks;
    friend class BleRxCallbacks;
};

#endif // BLE_SERIAL_H
