#include <Arduino.h>
#include "ble_serial.h"

// Maximum payload per BLE notification.
// Conservative default; NimBLE negotiates larger MTU but not all clients do.
static const size_t BLE_CHUNK_SIZE = 200;

// Keep a module-level pointer so NimBLE C-style callbacks can reach our instance.
static BleSerial* _instance = nullptr;

// ---------------------------------------------------------------------------
// NimBLE server callbacks — track connection state, restart advertising
// ---------------------------------------------------------------------------
class BleServerCallbacks : public NimBLEServerCallbacks {
    void onConnect(NimBLEServer* pServer, NimBLEConnInfo& connInfo) override {
        if (_instance) {
            _instance->_connected = true;
            Serial.printf("[BLE] Client connected (handle %d)\r\n", connInfo.getConnHandle());
        }
    }

    void onDisconnect(NimBLEServer* pServer, NimBLEConnInfo& connInfo, int reason) override {
        if (_instance) {
            _instance->_connected = false;
            Serial.printf("[BLE] Client disconnected (reason %d) — re-advertising\r\n", reason);
        }
        // Restart advertising so a new client can connect
        NimBLEDevice::startAdvertising();
    }
};

// ---------------------------------------------------------------------------
// NimBLE RX characteristic callback — line-buffer incoming writes
// ---------------------------------------------------------------------------
class BleRxCallbacks : public NimBLECharacteristicCallbacks {
    void onWrite(NimBLECharacteristic* pChar, NimBLEConnInfo& connInfo) override {
        if (!_instance || !_instance->_callback) return;

        std::string value = pChar->getValue();
        for (size_t i = 0; i < value.length(); i++) {
            char c = value[i];
            if (c == '\r' || c == '\n') {
                if (_instance->_rxBuffer.length() > 0) {
                    _instance->_callback(_instance->_rxBuffer);
                    _instance->_rxBuffer = "";
                }
            } else if (c >= 32 && c <= 126) {
                _instance->_rxBuffer += c;
            }
        }

        // If the sender doesn't terminate with newline, process whatever we got.
        // This handles single-shot writes like "R90" without a trailing \n.
        if (_instance->_rxBuffer.length() > 0) {
            _instance->_callback(_instance->_rxBuffer);
            _instance->_rxBuffer = "";
        }
    }
};

// ---------------------------------------------------------------------------
// Provisioning characteristic — Wi-Fi credentials and the backend token
// ---------------------------------------------------------------------------
class BleProvisionCallbacks : public NimBLECharacteristicCallbacks {
    void onWrite(NimBLECharacteristic* pChar, NimBLEConnInfo& connInfo) override {
        if (!_instance || !_instance->_provision) return;

        // Belt and braces. The characteristic is declared WRITE_ENC so the
        // stack should already have refused an unencrypted write, but this is
        // a Wi-Fi password and the check is one line.
        if (!connInfo.isEncrypted()) {
            Serial.println("[BLE] refused a provisioning write on an unencrypted link");
            return;
        }

        std::string value = pChar->getValue();
        for (size_t i = 0; i < value.length(); i++) {
            char c = value[i];
            if (c == '\r' || c == '\n') {
                if (_instance->_provisionBuffer.length() > 0) {
                    _instance->_provision(_instance->_provisionBuffer);
                    _instance->_provisionBuffer = "";
                }
            } else if (c >= 32 && c <= 126) {
                _instance->_provisionBuffer += c;
            }
        }
        // Same single-shot tolerance as the command characteristic: a client
        // that writes "Ussid|pass" with no newline still gets through.
        if (_instance->_provisionBuffer.length() > 0) {
            _instance->_provision(_instance->_provisionBuffer);
            _instance->_provisionBuffer = "";
        }
    }
};

// ---------------------------------------------------------------------------
// BleSerial public API
// ---------------------------------------------------------------------------

void BleSerial::begin(const char* deviceName, BleCommandCallback callback,
                      BleCommandCallback onProvision) {
    _callback = callback;
    _provision = onProvision;
    _instance = this;

    NimBLEDevice::init(deviceName);

    // Bonding and LE Secure Connections, without man-in-the-middle protection:
    // Marvin has no display and no keypad, so there is no way to show or check
    // a passkey. That leaves Just Works pairing, which is open to an active
    // attacker present at the moment of pairing but protects everything
    // afterwards — the right trade for a robot that is set up once, indoors.
    NimBLEDevice::setSecurityAuth(/*bonding=*/true, /*mitm=*/false, /*sc=*/true);

    _server = NimBLEDevice::createServer();
    _server->setCallbacks(new BleServerCallbacks());

    // Create Nordic UART Service
    NimBLEService* pService = _server->createService(NUS_SERVICE_UUID);

    // TX characteristic — server notifies client here
    _txChar = pService->createCharacteristic(
        NUS_TX_UUID,
        NIMBLE_PROPERTY::NOTIFY
    );

    // RX characteristic — client writes commands here
    NimBLECharacteristic* rxChar = pService->createCharacteristic(
        NUS_RX_UUID,
        NIMBLE_PROPERTY::WRITE | NIMBLE_PROPERTY::WRITE_NR
    );
    rxChar->setCallbacks(new BleRxCallbacks());

    // Provisioning characteristic, on builds that have something to provision.
    if (_provision) {
        NimBLECharacteristic* provChar = pService->createCharacteristic(
            NUS_PROVISION_UUID,
            NIMBLE_PROPERTY::WRITE | NIMBLE_PROPERTY::WRITE_ENC
        );
        provChar->setCallbacks(new BleProvisionCallbacks());
    }

    // Start the GATT server (registers all services with the NimBLE host stack)
    _server->start();

    // Configure advertising
    NimBLEAdvertising* pAdvertising = NimBLEDevice::getAdvertising();
    pAdvertising->addServiceUUID(NUS_SERVICE_UUID);
    pAdvertising->setName(deviceName);
    pAdvertising->start();

    Serial.printf("[BLE] Advertising as \"%s\"\r\n", deviceName);
}

void BleSerial::send(const char* data) {
    if (!_connected || !_txChar) return;

    size_t len = strlen(data);
    size_t offset = 0;

    while (offset < len) {
        size_t chunk = (len - offset > BLE_CHUNK_SIZE) ? BLE_CHUNK_SIZE : (len - offset);
        _txChar->setValue((const uint8_t*)(data + offset), chunk);
        _txChar->notify();
        offset += chunk;
    }
}

void BleSerial::send(const String& data) {
    send(data.c_str());
}

bool BleSerial::isConnected() const {
    return _connected;
}
