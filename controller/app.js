/* ==========================================================================
   Marvin Bluetooth Terminal — Application Logic
   ========================================================================== */

// Nordic UART Service UUIDs (must match firmware)
const NUS_SERVICE_UUID = '6e400001-b5a3-f393-e0a9-e50e24dcca9e';
const NUS_RX_UUID      = '6e400002-b5a3-f393-e0a9-e50e24dcca9e';  // Write to device
const NUS_TX_UUID      = '6e400003-b5a3-f393-e0a9-e50e24dcca9e';  // Notifications from device

// ============================================================================
// BleConnection — encapsulates all Web Bluetooth API interaction
// ============================================================================

class BleConnection {
    constructor() {
        this._device = null;
        this._server = null;
        this._rxChar = null;  // We write commands here
        this._txChar = null;  // We receive notifications here
        this._decoder = new TextDecoder();
        this._encoder = new TextEncoder();
        this._listeners = { data: [], connected: [], disconnected: [] };
    }

    /** Register an event listener: 'data', 'connected', 'disconnected' */
    on(event, callback) {
        if (this._listeners[event]) {
            this._listeners[event].push(callback);
        }
    }

    _emit(event, ...args) {
        (this._listeners[event] || []).forEach(cb => cb(...args));
    }

    /** True when a GATT connection is active */
    get isConnected() {
        return this._server && this._server.connected;
    }

    /** Name of the connected device, or null */
    get deviceName() {
        return this._device ? this._device.name : null;
    }

    /**
     * Scan for a Marvin device and connect.
     * The browser shows a native device picker filtered to NUS-advertising devices.
     */
    async scan() {
        if (!navigator.bluetooth) {
            throw new Error(
                'Web Bluetooth is not supported in this browser. Use Chrome or Edge.'
            );
        }

        // Request a device advertising the NUS service
        this._device = await navigator.bluetooth.requestDevice({
            filters: [{ services: [NUS_SERVICE_UUID] }],
        });

        // Listen for unexpected disconnects
        this._device.addEventListener(
            'gattserverdisconnected',
            () => this._onDisconnected()
        );

        await this._connect();
    }

    /** Internal: establish GATT connection and subscribe to notifications */
    async _connect() {
        this._server = await this._device.gatt.connect();
        const service = await this._server.getPrimaryService(NUS_SERVICE_UUID);

        // RX characteristic — we write commands here
        this._rxChar = await service.getCharacteristic(NUS_RX_UUID);

        // TX characteristic — firmware sends responses here via notifications
        this._txChar = await service.getCharacteristic(NUS_TX_UUID);
        await this._txChar.startNotifications();
        this._txChar.addEventListener(
            'characteristicvaluechanged',
            (event) => this._onData(event)
        );

        this._emit('connected', this._device.name);
    }

    /** Send a command string to the device (appends newline) */
    async send(command) {
        if (!this._rxChar) {
            throw new Error('Not connected');
        }
        const data = this._encoder.encode(command + '\n');
        await this._rxChar.writeValueWithResponse(data);
    }

    /** Disconnect from the device */
    disconnect() {
        if (this._device && this._device.gatt.connected) {
            this._device.gatt.disconnect();
        }
    }

    /** Handle incoming notification data */
    _onData(event) {
        const value = event.target.value;
        const text = this._decoder.decode(value);
        this._emit('data', text);
    }

    /** Handle disconnection (explicit or unexpected) */
    _onDisconnected() {
        this._server = null;
        this._rxChar = null;
        this._txChar = null;
        this._emit('disconnected');
    }
}

// ============================================================================
// Terminal — manages output pane, input field, and command history
// ============================================================================

class Terminal {
    constructor(outputEl, inputEl) {
        this._output = outputEl;
        this._input = inputEl;
        this._history = [];
        this._historyIndex = -1;
        this._listeners = { command: [] };

        this._input.addEventListener('keydown', (e) => this._onKeyDown(e));
    }

    on(event, callback) {
        if (this._listeners[event]) {
            this._listeners[event].push(callback);
        }
    }

    _emit(event, ...args) {
        (this._listeners[event] || []).forEach(cb => cb(...args));
    }

    /** Append a line to the terminal output */
    appendLine(text, className = '') {
        const line = document.createElement('div');
        line.classList.add('line');
        if (className) line.classList.add(className);
        line.textContent = text;
        this._output.appendChild(line);
        this._scrollToBottom();
    }

    /** Append a command echo */
    appendCommand(cmd) {
        this.appendLine(cmd, 'cmd-line');
    }

    /** Append a response from the device */
    appendResponse(text) {
        // Split by lines in case firmware sends multi-line responses
        const lines = text.split(/\r?\n/);
        for (const l of lines) {
            if (l.length > 0) {
                this.appendLine(l, 'response-line');
            }
        }
    }

    /** Append a system/info message */
    appendSystem(text) {
        this.appendLine(text, 'system-line');
    }

    /** Append an error message */
    appendError(text) {
        this.appendLine(text, 'error-line');
    }

    /** Clear all output */
    clear() {
        this._output.innerHTML = '';
    }

    /** Enable or disable the input field */
    setEnabled(enabled) {
        this._input.disabled = !enabled;
        if (enabled) this._input.focus();
    }

    /** Focus the input */
    focus() {
        this._input.focus();
    }

    /** Submit the current input value */
    submit() {
        const cmd = this._input.value.trim();
        if (!cmd) return;

        this.appendCommand(cmd);
        this._history.push(cmd);
        this._historyIndex = this._history.length;
        this._input.value = '';

        this._emit('command', cmd);
    }

    _scrollToBottom() {
        requestAnimationFrame(() => {
            this._output.scrollTop = this._output.scrollHeight;
        });
    }

    _onKeyDown(e) {
        if (e.key === 'ArrowUp') {
            e.preventDefault();
            if (this._historyIndex > 0) {
                this._historyIndex--;
                this._input.value = this._history[this._historyIndex];
            }
        } else if (e.key === 'ArrowDown') {
            e.preventDefault();
            if (this._historyIndex < this._history.length - 1) {
                this._historyIndex++;
                this._input.value = this._history[this._historyIndex];
            } else {
                this._historyIndex = this._history.length;
                this._input.value = '';
            }
        }
    }
}

// ============================================================================
// App — wires everything together
// ============================================================================

class App {
    constructor() {
        // DOM elements
        this._scanBtn       = document.getElementById('scanBtn');
        this._disconnectBtn = document.getElementById('disconnectBtn');
        this._clearBtn      = document.getElementById('clearBtn');
        this._sendBtn       = document.getElementById('sendBtn');
        this._inputForm     = document.getElementById('inputForm');
        this._statusBadge   = document.getElementById('statusBadge');
        this._statusText    = this._statusBadge.querySelector('.status-text');
        this._terminalTitle = document.getElementById('terminalTitle');

        // Core objects
        this._ble = new BleConnection();
        this._terminal = new Terminal(
            document.getElementById('terminalOutput'),
            document.getElementById('commandInput')
        );

        this._bindEvents();
    }

    _bindEvents() {
        // Button clicks
        this._scanBtn.addEventListener('click', () => this._onScan());
        this._disconnectBtn.addEventListener('click', () => this._onDisconnect());
        this._clearBtn.addEventListener('click', () => this._terminal.clear());

        // Form submit (Enter key or Send button)
        this._inputForm.addEventListener('submit', (e) => {
            e.preventDefault();
            this._onSendCommand();
        });

        // Quick command buttons
        document.querySelectorAll('.btn-quick').forEach(btn => {
            btn.addEventListener('click', () => {
                const cmd = btn.dataset.cmd;
                if (cmd && this._ble.isConnected) {
                    this._sendCommand(cmd);
                }
            });
        });

        // BLE events
        this._ble.on('connected', (name) => this._onConnected(name));
        this._ble.on('disconnected', () => this._onDisconnected());
        this._ble.on('data', (text) => this._terminal.appendResponse(text));

        // Terminal command event
        this._terminal.on('command', (cmd) => this._sendCommand(cmd));
    }

    async _onScan() {
        try {
            this._terminal.appendSystem('Scanning for Marvin devices...');
            this._scanBtn.disabled = true;
            await this._ble.scan();
        } catch (err) {
            if (err.name === 'NotFoundError') {
                this._terminal.appendSystem('No device selected.');
            } else {
                this._terminal.appendError('Error: ' + err.message);
            }
            this._scanBtn.disabled = false;
        }
    }

    _onDisconnect() {
        this._ble.disconnect();
    }

    async _sendCommand(cmd) {
        try {
            await this._ble.send(cmd);
        } catch (err) {
            this._terminal.appendError('Send failed: ' + err.message);
        }
    }

    _onSendCommand() {
        if (!this._ble.isConnected) return;
        this._terminal.submit();
    }

    _onConnected(deviceName) {
        this._terminal.appendSystem(`Connected to ${deviceName}`);
        this._statusBadge.classList.add('connected');
        this._statusText.textContent = deviceName;
        this._terminalTitle.textContent = `marvin — ${deviceName}`;
        this._scanBtn.disabled = true;
        this._disconnectBtn.disabled = false;
        this._sendBtn.disabled = false;
        this._terminal.setEnabled(true);
    }

    _onDisconnected() {
        this._terminal.appendSystem('Disconnected');
        this._statusBadge.classList.remove('connected');
        this._statusText.textContent = 'Disconnected';
        this._terminalTitle.textContent = 'marvin — not connected';
        this._scanBtn.disabled = false;
        this._disconnectBtn.disabled = true;
        this._sendBtn.disabled = true;
        this._terminal.setEnabled(false);
    }
}

// Boot
document.addEventListener('DOMContentLoaded', () => new App());
