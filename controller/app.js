/* ==========================================================================
   Marvin Bluetooth Terminal — Application Logic
   ========================================================================== */

// Nordic UART Service UUIDs (must match firmware)
const NUS_SERVICE_UUID = '6e400001-b5a3-f393-e0a9-e50e24dcca9e';
const NUS_RX_UUID      = '6e400002-b5a3-f393-e0a9-e50e24dcca9e';  // Write to device
const NUS_TX_UUID      = '6e400003-b5a3-f393-e0a9-e50e24dcca9e';  // Notifications from device

// Provisioning — Marvin's own, not part of the Nordic standard.
//
// Separate from the command characteristic because it requires an encrypted
// link: Wi-Fi passwords and the backend token go through here, and an
// unencrypted BLE write is readable from the next room. Writing to it is also
// what makes the browser and the operating system pair in the first place.
const NUS_PROVISION_UUID = '6e400004-b5a3-f393-e0a9-e50e24dcca9e';

// ============================================================================
// BleConnection — encapsulates all Web Bluetooth API interaction
// ============================================================================

class BleConnection {
    constructor() {
        this._device = null;
        this._server = null;
        this._rxChar = null;    // We write commands here
        this._txChar = null;    // We receive notifications here
        this._provChar = null;  // Wi-Fi and token, over an encrypted link
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

        // Provisioning is optional: a robot on the drive-only firmware has no
        // such characteristic, and that is not an error — it simply cannot be
        // set up for voice.
        try {
            this._provChar = await service.getCharacteristic(NUS_PROVISION_UUID);
        } catch {
            this._provChar = null;
        }

        this._emit('connected', this._device.name);
    }

    /** True when this robot's firmware can be provisioned for voice */
    get canProvision() {
        return this._provChar !== null;
    }

    /**
     * Send a provisioning line. The first call prompts the browser and the
     * operating system to pair, because the characteristic requires encryption.
     */
    async sendProvision(line) {
        if (!this._provChar) {
            throw new Error(
                'This robot\'s firmware has no provisioning characteristic — ' +
                'it is a drive-only build without voice.'
            );
        }
        const data = this._encoder.encode(line + '\n');
        await this._provChar.writeValueWithResponse(data);
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
        this._provChar = null;
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
// Backend — the server Marvin talks to
// ============================================================================
//
// Available only when this page is served by that server, which is the normal
// case. Opened from a plain file server for local work, every call below fails
// and the app falls back to Bluetooth-only: you can still drive the robot and
// save Wi-Fi, you just cannot mint a token or watch a conversation.

class Backend {
    constructor() {
        this._listeners = { update: [], reachable: [] };
        this._socket = null;
        this._reachable = null;
    }

    on(event, callback) {
        if (this._listeners[event]) this._listeners[event].push(callback);
    }

    _emit(event, ...args) {
        (this._listeners[event] || []).forEach(cb => cb(...args));
    }

    get reachable() { return this._reachable === true; }

    async _json(method, path, body) {
        const res = await fetch(path, {
            method,
            headers: body ? { 'Content-Type': 'application/json' } : {},
            body: body ? JSON.stringify(body) : undefined,
            // A signed-out session is answered with a redirect to the sign-in
            // page. Following it would hand us an HTML document where JSON was
            // expected; catching it here gives a message that makes sense.
            redirect: 'manual',
        });
        if (res.type === 'opaqueredirect' || res.status === 401 || res.status === 403) {
            throw new Error('Signed out — reload the page to sign in again.');
        }
        const text = await res.text();
        let data = {};
        try { data = text ? JSON.parse(text) : {}; } catch { /* not JSON */ }
        if (!res.ok) throw new Error(data.error || `Server returned ${res.status}`);
        return data;
    }

    async status() {
        try {
            const s = await this._json('GET', '/api/status');
            this._setReachable(true);
            return s;
        } catch (err) {
            this._setReachable(false);
            throw err;
        }
    }

    setProvider(name) {
        return this._json('POST', '/api/provider', { provider: name });
    }

    deviceToken(deviceId) {
        return this._json('POST', '/api/device-token', { device_id: deviceId });
    }

    /** Tell a connected robot to do something. */
    deviceAction(deviceId, action) {
        return this._json('POST', '/api/device-action', { device_id: deviceId, ...action });
    }

    _setReachable(value) {
        if (this._reachable === value) return;
        this._reachable = value;
        this._emit('reachable', value);
    }

    /** Watch live updates. Reconnects on its own; a dropped view is not an error. */
    watch() {
        if (this._socket) return;
        const scheme = location.protocol === 'https:' ? 'wss' : 'ws';
        const socket = new WebSocket(`${scheme}://${location.host}/ws/controller`);
        this._socket = socket;

        socket.addEventListener('message', (e) => {
            try {
                this._emit('update', JSON.parse(e.data));
            } catch { /* ignore a malformed frame */ }
        });
        socket.addEventListener('close', () => {
            this._socket = null;
            setTimeout(() => this.watch(), 5000);
        });
        socket.addEventListener('error', () => socket.close());
    }
}

// ============================================================================
// App — wires everything together
// ============================================================================

class App {
    constructor() {
        this._el = (id) => document.getElementById(id);

        this._scanBtn       = this._el('scanBtn');
        this._disconnectBtn = this._el('disconnectBtn');
        this._clearBtn      = this._el('clearBtn');
        this._sendBtn       = this._el('sendBtn');
        this._inputForm     = this._el('inputForm');
        this._statusBadge   = this._el('statusBadge');
        this._statusText    = this._statusBadge.querySelector('.status-text');
        this._terminalTitle = this._el('terminalTitle');
        this._toast         = this._el('toast');

        this._ble = new BleConnection();
        this._backend = new Backend();
        this._terminal = new Terminal(this._el('terminalOutput'), this._el('commandInput'));

        // Marvin's replies arrive as a stream of notifications, so a line can
        // be split across several. Buffered here and cut on newlines, or the
        // setup readout arrives one word at a time.
        this._readoutBuffer = '';
        this._captureReadout = false;

        // Bluetooth notifications do not respect line boundaries either, and
        // the [WIFI] and [NET] reports have to be whole lines to be parsed.
        this._lineBuffer = '';
        this._networks = [];
        this._wifiState = { phase: 'unknown', ssid: null, ip: null, rssi: null, error: null };
        this._scanning = false;

        // Whether the person has typed their own backend address. Once they
        // have, the server's suggestion stops overwriting it.
        this._urlTouched = false;

        this._bindEvents();
        this._prefillBackendUrl();
        this._loadStatus();
        this._backend.watch();
    }

    // --- wiring -------------------------------------------------------------

    _bindEvents() {
        this._scanBtn.addEventListener('click', () => this._onScan());
        this._disconnectBtn.addEventListener('click', () => this._ble.disconnect());
        this._clearBtn.addEventListener('click', () => this._terminal.clear());

        this._inputForm.addEventListener('submit', (e) => {
            e.preventDefault();
            if (this._ble.isConnected) this._terminal.submit();
        });

        document.querySelectorAll('.btn-quick').forEach((btn) => {
            btn.addEventListener('click', () => {
                if (btn.dataset.cmd && this._ble.isConnected) this._sendCommand(btn.dataset.cmd);
            });
        });

        document.querySelectorAll('.tab').forEach((tab) => {
            tab.addEventListener('click', () => this._showPane(tab.dataset.pane));
        });

        this._el('backendUrl').addEventListener('input', () => { this._urlTouched = true; });
        this._el('rescanBtn').addEventListener('click', () => this._scanNetworks());
        this._el('wifiSsid').addEventListener('change', () => this._onSsidChanged());
        this._el('wifiForm').addEventListener('submit', (e) => this._onSaveWiFi(e));
        this._el('backendForm').addEventListener('submit', (e) => this._onSaveBackend(e));
        this._el('statusBtn').addEventListener('click', () => this._onAskStatus());
        this._el('forgetBtn').addEventListener('click', () => this._onForget());
        this._el('clearLogBtn').addEventListener('click', () => this._clearTranscript());

        this._ble.on('connected', (name) => this._onConnected(name));
        this._ble.on('disconnected', () => this._onDisconnected());
        this._ble.on('data', (text) => this._onBleData(text));

        this._terminal.on('command', (cmd) => this._sendCommand(cmd));

        this._backend.on('update', (u) => this._onBackendUpdate(u));
        this._backend.on('reachable', (ok) => this._onBackendReachable(ok));
    }

    _showPane(name) {
        document.querySelectorAll('.tab').forEach((t) => {
            t.classList.toggle('tab-active', t.dataset.pane === name);
        });
        document.querySelectorAll('.pane').forEach((p) => {
            const active = p.id === `pane-${name}`;
            p.classList.toggle('pane-active', active);
            p.hidden = !active;
        });
        if (name === 'drive' && this._ble.isConnected) this._terminal.focus();
        if (name === 'voice') this._loadStatus();
        // Scan when the pane is opened rather than on connect: the first
        // provisioning write is what prompts the browser to pair, and someone
        // who only wants to drive should not be asked to.
        if (name === 'setup' && this._ble.canProvision && !this._networks.length) {
            this._scanNetworks();
        }
    }

    _toastMessage(text, kind = 'ok') {
        this._toast.textContent = text;
        this._toast.className = `toast toast-${kind}`;
        this._toast.hidden = false;
        clearTimeout(this._toastTimer);
        this._toastTimer = setTimeout(() => { this._toast.hidden = true; }, 5000);
    }

    // --- Bluetooth ----------------------------------------------------------

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
                this._toastMessage(err.message, 'error');
            }
            this._scanBtn.disabled = false;
        }
    }

    async _sendCommand(cmd) {
        try {
            await this._ble.send(cmd);
        } catch (err) {
            this._terminal.appendError('Send failed: ' + err.message);
        }
    }

    _onBleData(text) {
        if (this._captureReadout) {
            this._readoutBuffer += text;
            this._el('setupReadout').textContent = this._readoutBuffer.trimEnd() || '…';
        }

        this._lineBuffer += text;
        const lines = this._lineBuffer.split(/\r?\n/);
        // Whatever follows the last newline is an unfinished line; keep it.
        this._lineBuffer = lines.pop();

        for (const line of lines) {
            // A scan produces two dozen of these. They are data for the
            // dropdown, and printing them would bury everything else in the
            // terminal — unlike the [WIFI] state lines, which are worth seeing.
            if (line.startsWith('[NET] ')) {
                this._onScanResult(line.slice(6));
                continue;
            }
            this._terminal.appendResponse(line);
            if (line.startsWith('[WIFI] ')) this._onWifiEvent(line.slice(7).trim());
        }

        // Show a partial line as it arrives so typing feels responsive; it is
        // replaced when the full line lands.
        if (this._lineBuffer && !this._lineBuffer.startsWith('[')) {
            this._terminal.appendResponse(this._lineBuffer);
            this._lineBuffer = '';
        }
    }

    // --- Wi-Fi -------------------------------------------------------------

    _onScanResult(payload) {
        const [rssi, security, ...rest] = payload.split('|');
        const ssid = rest.join('|');
        if (!ssid) return;

        // A network seen on several channels reports once per channel; keep the
        // strongest sighting of each name.
        const existing = this._networks.find((n) => n.ssid === ssid);
        if (existing) {
            existing.rssi = Math.max(existing.rssi, Number(rssi));
        } else {
            this._networks.push({ ssid, rssi: Number(rssi), open: security === 'open' });
        }
    }

    _onWifiEvent(event) {
        const [kind, ...args] = event.split(' ');
        const st = this._wifiState;

        switch (kind) {
        case 'scanning':
            this._scanning = true;
            this._networks = [];
            this._renderWifi('scanning');
            return;
        case 'scan_done':
            this._scanning = false;
            this._renderNetworks();
            this._renderWifi();
            return;
        case 'scan_failed':
        case 'scan_busy':
            this._scanning = false;
            this._toastMessage(kind === 'scan_busy'
                ? 'Marvin is mid-connection; try Rescan in a moment.'
                : 'The scan failed. Try Rescan.', 'error');
            this._renderWifi();
            return;
        case 'connecting':
            st.phase = 'connecting'; st.ssid = args.join(' '); st.error = null;
            break;
        case 'connected':
            st.phase = 'connected'; st.ip = args[0]; st.rssi = args[1]; st.error = null;
            break;
        case 'failed':
            st.phase = 'failed'; st.error = args[0];
            break;
        case 'disconnected':
            st.phase = 'disconnected';
            break;
        case 'retry':
            st.phase = 'retrying'; st.retryMs = Number(args[0]);
            break;
        case 'unconfigured':
            st.phase = 'unconfigured';
            break;
        default:
            return;
        }
        this._renderWifi();
    }

    _renderWifi(override) {
        const chip = this._el('wifiChip');
        const note = this._el('wifiNote');
        const st = this._wifiState;
        const phase = override || st.phase;

        const faults = {
            auth: 'Marvin was refused — the password is probably wrong.',
            notfound: 'That network was not found. It may be 5 GHz only, or out of range.',
            timeout: 'The attempt timed out. The network is there but did not finish letting Marvin in.',
            other: 'The connection failed.',
        };

        const views = {
            scanning:     ['scanning…',    'chip-busy',    'Asking Marvin what it can see.'],
            connecting:   [`connecting…`,  'chip-busy',    `Joining “${st.ssid || ''}”.`],
            connected:    ['connected',    'chip-ok',      `On “${st.ssid || 'the network'}” at ${st.ip} (${st.rssi} dBm).`],
            failed:       ['failed',       'chip-bad',     faults[st.error] || faults.other],
            retrying:     ['retrying…',    'chip-busy',    `${faults[st.error] || faults.other} Trying again in ${Math.round((st.retryMs || 0) / 1000)}s.`],
            disconnected: ['disconnected', 'chip-bad',     'The network went away. Marvin will keep trying.'],
            unconfigured: ['not set up',   'chip-unknown', 'No network saved yet. Pick one and press Connect.'],
            unknown:      ['not connected','chip-unknown', ''],
        };

        const [label, cls, message] = views[phase] || views.unknown;
        chip.textContent = label;
        chip.className = `chip ${cls}`;
        if (message) note.textContent = message;
    }

    _renderNetworks() {
        const select = this._el('wifiSsid');
        const previous = select.value;
        this._networks.sort((a, b) => b.rssi - a.rssi);

        select.innerHTML = '';
        if (!this._networks.length) {
            select.appendChild(new Option('No networks found', ''));
        }
        for (const n of this._networks) {
            const bars = n.rssi > -55 ? '▂▄▆█' : n.rssi > -67 ? '▂▄▆' : n.rssi > -78 ? '▂▄' : '▂';
            select.appendChild(new Option(
                `${n.ssid}  ${bars}${n.open ? '  (open)' : ''}`, n.ssid));
        }
        // A hidden network has no name to broadcast, so it can only be typed.
        select.appendChild(new Option('Other — type the name…', '__manual__'));

        select.disabled = false;
        if (previous && [...select.options].some((o) => o.value === previous)) {
            select.value = previous;
        }
        this._onSsidChanged();
    }

    _onSsidChanged() {
        const manual = this._el('wifiSsid').value === '__manual__';
        this._el('ssidManualRow').hidden = !manual;
        if (manual) this._el('wifiSsidManual').focus();
    }

    async _scanNetworks() {
        if (!this._ble.canProvision || this._scanning) return;
        try {
            this._renderWifi('scanning');
            await this._ble.sendProvision('Q');
        } catch (err) {
            this._scanning = false;
            this._toastMessage(err.message, 'error');
            this._renderWifi();
        }
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
        this._setSetupEnabled(true);

        if (!this._ble.canProvision) {
            this._el('setupReadout').textContent =
                'This robot is running the drive-only firmware. Setup needs the voice ' +
                'build — flash the esp32s3-supermini environment.';
        } else {
            this._el('setupReadout').textContent = 'Connected. Nothing asked yet.';
            this._el('deviceId').value = deviceName || 'marvin';
        }
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
        this._setSetupEnabled(false);
        this._el('setupReadout').textContent = 'Not connected.';
    }

    _setSetupEnabled(enabled) {
        const on = enabled && this._ble.canProvision;
        this._el('wifiForm').querySelector('button').disabled = !on;
        this._el('rescanBtn').disabled = !on;
        this._el('statusBtn').disabled = !enabled;
        this._el('forgetBtn').disabled = !on;
        // The backend button needs a reachable backend as well as a robot.
        this._el('backendForm').querySelector('button').disabled = !on || !this._backend.reachable;

        if (!on) {
            this._networks = [];
            const select = this._el('wifiSsid');
            select.innerHTML = '';
            select.appendChild(new Option('Connect over Bluetooth first', ''));
            select.disabled = true;
        }
    }

    // --- setup --------------------------------------------------------------

    _prefillBackendUrl() {
        // A first guess, replaced by the server's own answer as soon as
        // /api/status responds. It matters when the two differ: on a bench the
        // controller is open at localhost — Web Bluetooth will not run anywhere
        // else without HTTPS — and localhost is the one address the robot
        // certainly cannot reach.
        const scheme = location.protocol === 'https:' ? 'wss' : 'ws';
        this._el('backendUrl').value = `${scheme}://${location.host}/v1/device`;
    }

    async _onSaveWiFi(e) {
        e.preventDefault();
        const choice = this._el('wifiSsid').value;
        const ssid = (choice === '__manual__' ? this._el('wifiSsidManual').value : choice).trim();
        const password = this._el('wifiPassword').value;

        if (!ssid) {
            this._toastMessage('Pick a network first.', 'error');
            return;
        }
        if (ssid.includes('|')) {
            this._toastMessage('A network name containing "|" cannot be sent — it separates the fields.', 'error');
            return;
        }
        try {
            this._wifiState.ssid = ssid;
            await this._ble.sendProvision(`N${ssid}|${password}`);
            // Cleared as soon as it has been sent. It is on the robot now, and
            // a password sitting in a form field is a password on a screen.
            this._el('wifiPassword').value = '';
            this._renderWifi('connecting');
            // No toast: the robot reports what actually happened within a few
            // seconds, and "saved" would be claiming success we do not have yet.
        } catch (err) {
            this._toastMessage(err.message, 'error');
        }
    }

    async _onSaveBackend(e) {
        e.preventDefault();
        const deviceId = this._el('deviceId').value.trim();
        const url = this._el('backendUrl').value.trim();
        if (!deviceId || !url) return;

        if (!this._backend.reachable) {
            this._toastMessage(this._backendUnreachableAdvice(), 'error');
            return;
        }

        try {
            // Mint first. Writing a URL the robot then has no token for leaves
            // it retrying a connection that can only ever be refused.
            const issued = await this._backend.deviceToken(deviceId);
            await this._ble.sendProvision(`I${deviceId}`);
            await this._ble.sendProvision(`U${url}`);
            await this._ble.sendProvision(`K${issued.token}`);
            this._toastMessage('Backend saved. Power-cycle Marvin to connect.');
            this._el('setupReadout').textContent =
                `Token issued for "${issued.device_id}", valid until ${issued.expires}.\n` +
                'Power-cycle Marvin, then press "Ask the robot how it is doing".';
        } catch (err) {
            this._toastMessage(err.message, 'error');
        }
    }

    // Where to actually go. The usual cause of an unreachable backend is having
    // opened the controller from the static dev server on port 3000, which
    // serves the files and nothing else — so say that, rather than the truth in
    // the abstract.
    _backendUnreachableAdvice() {
        const onDevServer = location.port === '3000';
        return onDevServer
            ? 'This page is the static dev server on port 3000, which has no backend behind it. '
              + 'Start the backend (./run_local.sh) and open http://localhost:8080/app/ instead.'
            : 'No backend answered at ' + location.origin + '. Open the controller from the '
              + 'server Marvin connects to.';
    }

    async _onAskStatus() {
        this._readoutBuffer = '';
        this._captureReadout = true;
        this._el('setupReadout').textContent = '…';
        await this._sendCommand('?');
        // The reply arrives as several notifications; stop collecting once they
        // have had time to land.
        setTimeout(() => { this._captureReadout = false; }, 1500);
    }

    async _onForget() {
        if (!confirm('Clear the Wi-Fi credentials, backend address and token stored on this robot?')) {
            return;
        }
        try {
            await this._ble.sendProvision('X!');
            this._toastMessage('Settings cleared. Restart Marvin.');
        } catch (err) {
            this._toastMessage(err.message, 'error');
        }
    }

    // --- voice --------------------------------------------------------------

    async _loadStatus() {
        try {
            const status = await this._backend.status();
            this._renderProviders(status);
            this._renderDevices(status.devices || []);
            if (status.wake_word) this._el('wakeWord').textContent = `“${status.wake_word}”`;
            if (status.device_url && !this._urlTouched) {
                this._el('backendUrl').value = status.device_url;
            }
            this._renderLocalNote(status);
        } catch (err) {
            this._el('providers').innerHTML =
                `<p class="card-note">Cannot reach the backend: ${escapeHtml(err.message)}</p>`;
        }
    }

    _renderLocalNote(status) {
        const note = this._el('backendNote');
        if (!status.local) return;

        const url = status.device_url || '';
        note.innerHTML =
            '<strong>Local mode.</strong> The sign-in is off and this server is ' +
            'only reachable from this network. The address below is this ' +
            "machine's LAN address, not <code>localhost</code> — the robot has to " +
            'be able to route to it.' +
            (url.startsWith('ws://')
                ? ' It is plain <code>ws://</code>, so the device token is sent ' +
                  'unencrypted: fine on your own network, not beyond it.'
                : '');
    }

    _renderProviders(status) {
        const host = this._el('providers');
        host.innerHTML = '';

        for (const p of status.providers || []) {
            const row = document.createElement('button');
            row.className = 'provider' +
                (p.active ? ' provider-active' : '') +
                (p.configured ? '' : ' provider-disabled');
            row.disabled = !p.configured || p.active;
            row.innerHTML =
                `<span class="provider-name">${escapeHtml(p.name)}</span>` +
                `<span class="provider-model">${escapeHtml(status.model || p.default_model)}</span>` +
                `<span class="provider-state">${p.active ? 'in use' : p.configured ? 'available' : 'no API key'}</span>`;
            row.addEventListener('click', async () => {
                try {
                    this._renderProviders(await this._backend.setProvider(p.name));
                    this._toastMessage(`Switched to ${p.name}.`);
                } catch (err) {
                    this._toastMessage(err.message, 'error');
                }
            });
            host.appendChild(row);
        }
    }

    _renderDevices(devices) {
        const host = this._el('devices');
        if (!devices.length) {
            host.innerHTML =
                '<p class="card-note">None connected. A robot appears here once it ' +
                'has joined Wi-Fi and reached this server.</p>';
            return;
        }
        host.innerHTML = '';
        for (const d of devices) {
            const row = document.createElement('div');
            row.className = 'device';

            const bits = [];
            if (d.firmware) bits.push(escapeHtml(d.firmware));
            if (d.rssi) bits.push(`${d.rssi} dBm`);
            if (d.provider) bits.push(escapeHtml(d.provider));

            row.innerHTML =
                `<span class="device-dot${d.in_turn ? ' device-dot-live' : ''}"></span>` +
                `<span class="device-name">${escapeHtml(d.id)}</span>` +
                `<span class="device-meta">${bits.join(' · ')}</span>`;

            // The button is here for every robot, not only the ones with no
            // wake word: starting a conversation from the page is useful when
            // you are not in the room, and it is the only way in at all on a
            // board that cannot listen for its name.
            const talk = document.createElement('button');
            talk.className = 'btn btn-talk' + (d.in_turn ? ' btn-talk-live' : '');
            talk.textContent = d.in_turn ? 'Stop' : 'Start listening';
            talk.addEventListener('click', () => this._toggleListening(d, talk));
            row.appendChild(talk);

            host.appendChild(row);
        }
    }

    // W1 and W0 rather than a bare W toggle: a toggle desynchronises the moment
    // one message goes missing, and then the button says the opposite of what
    // the robot is doing.
    async _toggleListening(device, button) {
        const starting = !device.in_turn;
        button.disabled = true;
        try {
            await this._backend.deviceAction(device.id, { cmd: starting ? 'W1' : 'W0' });
            this._toastMessage(starting
                ? `${device.id} is listening — say something.`
                : `Asked ${device.id} for a reply.`);
        } catch (err) {
            this._toastMessage(err.message, 'error');
        } finally {
            button.disabled = false;
            this._loadStatus();
        }
    }

    _onBackendUpdate(u) {
        if (u.event === 'connected' || u.event === 'disconnected' || u.event === 'state') {
            this._loadStatus();
        }
        if (u.event === 'observation' && u.observation) {
            this._appendObservation(u.observation);
        }
    }

    _onBackendReachable(ok) {
        const chip = this._el('backendChip');
        chip.textContent = ok ? 'reachable' : 'not reachable';
        chip.className = `chip ${ok ? 'chip-ok' : 'chip-bad'}`;

        // The button cannot work without a backend to mint the token, so
        // disable it rather than let it fail with a 404 nobody can act on.
        const button = this._el('backendForm').querySelector('button');
        button.disabled = !ok || !this._ble.canProvision || !this._ble.isConnected;

        this._el('backendNote').textContent = ok
            ? 'The token never appears on this page and is never shown in a form anyone '
              + 'could copy — it authorises a live microphone.'
            : this._backendUnreachableAdvice();
    }

    _appendObservation(o) {
        const host = this._el('transcript');
        if (host.querySelector('.card-note')) host.innerHTML = '';

        // Transcripts stream in fragments. Appending to the previous line of
        // the same speaker turns a stutter of single words back into a
        // sentence.
        const last = host.lastElementChild;
        if (o.kind === 'transcript' && last && last.dataset.role === o.role) {
            last.querySelector('.said').textContent += o.text;
        } else {
            const line = document.createElement('div');
            line.className = `said-line said-${o.kind}` + (o.role ? ` said-${o.role}` : '');
            line.dataset.role = o.role || o.kind;
            line.innerHTML =
                `<span class="who">${escapeHtml(o.role || o.kind)}</span>` +
                `<span class="said">${escapeHtml(o.text || '')}</span>`;
            host.appendChild(line);
        }
        host.scrollTop = host.scrollHeight;
    }

    _clearTranscript() {
        this._el('transcript').innerHTML = '<p class="card-note">Nothing yet.</p>';
    }
}

// Everything rendered from the server or from a robot is treated as text, not
// markup. Device names and transcripts both come from outside this page.
function escapeHtml(value) {
    const div = document.createElement('div');
    div.textContent = value === undefined || value === null ? '' : String(value);
    return div.innerHTML;
}

// Boot
document.addEventListener('DOMContentLoaded', () => new App());
