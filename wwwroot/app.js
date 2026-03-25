// === HamDeck v3 — Web Control Dashboard ===
// Full rig control with session authentication.
// Polls the HamDeck API and sends commands via the same REST endpoints
// that Stream Deck uses, ensuring GUI/API/Web stay in sync.
//
// Dependencies loaded before this file: ptt.js, audio.js

const API = window.location.origin;
const POLL_FAST    = 500;   // freq, mode, tx, meters: 500ms
const POLL_SLOW    = 2500;  // filters, toggles, antenna: 2.5s
const POLL_SESSION = 3000;  // session stats: 3s
const POLL_CLUSTER = 30000; // dx cluster: 30s

// ===== AUTH CHECK =====

let sessionToken = null;
let isAdmin = false;

async function checkAuth() {
    try {
        const resp = await fetch(API + '/api/auth/status');
        const data = await resp.json();
        if (!data.authenticated) {
            window.location.href = '/login.html';
            return false;
        }
        sessionToken = data.token;
        window.sessionToken = sessionToken; // shared with audio.js
        isAdmin = data.is_admin;

        if (isAdmin) {
            const adminLink = document.getElementById('btn-admin');
            if (adminLink) adminLink.style.display = 'inline-block';
        }
        return true;
    } catch {
        return true;
    }
}

// ===== API HELPER =====

async function api(path) {
    try {
        const resp = await fetch(API + path);
        if (resp.status === 401) {
            window.location.href = '/login.html';
            return null;
        }
        return await resp.json();
    } catch {
        return null;
    }
}

window.api = api;

// ===== DOM REFS =====

const $ = (id) => document.getElementById(id);
const dom = {
    loading:    $('loading'),
    connDot:    $('conn-dot'),
    connText:   $('conn-text'),
    clock:      $('clock'),
    freq:       $('freq-display'),
    badgeBand:  $('badge-band'),
    badgeMode:  $('badge-mode'),
    badgeVfo:   $('badge-vfo'),
    badgeSplit: $('badge-split'),
    badgeTx:    $('badge-tx'),
    powerWatts: $('power-watts'),
    sFill:   $('s-fill'),    sVal:   $('s-val'),
    pwrFill: $('pwr-fill'),  pwrVal: $('pwr-val'),
    swrFill: $('swr-fill'),  swrVal: $('swr-val'),
    alcFill: $('alc-fill'),  alcVal: $('alc-val'),
    volVal:  $('vol-val'),
    cwVal:   $('cw-val'),
    ritOff:  $('rit-offset'),
    statTx:  $('stat-tx'),
    statTime:$('stat-time'),
    statQsy: $('stat-qsy'),
    clusterBody: $('cluster-body'),
    recDot:  $('rec-dot'),
};

// ===== FREQUENCY FORMATTING =====

function formatFreq(hz) {
    if (!hz || hz <= 0) return '—.———.——';
    const mhz = Math.floor(hz / 1000000);
    const khz = Math.floor((hz % 1000000) / 1000);
    const sub = Math.floor((hz % 1000) / 10);
    return `<span class="mhz">${mhz}</span>.${String(khz).padStart(3, '0')}.${String(sub).padStart(2, '0')}`;
}

function freqToBand(hz) {
    if (!hz) return '—';
    const m = hz / 1e6;
    if (m >= 1.8  && m <= 2.0)   return '160';
    if (m >= 3.5  && m <= 4.0)   return '80';
    if (m >= 5.3  && m <= 5.4)   return '60';
    if (m >= 7.0  && m <= 7.3)   return '40';
    if (m >= 10.1 && m <= 10.15) return '30';
    if (m >= 14.0 && m <= 14.35) return '20';
    if (m >= 18.068 && m <= 18.168) return '17';
    if (m >= 21.0 && m <= 21.45) return '15';
    if (m >= 24.89 && m <= 24.99) return '12';
    if (m >= 28.0 && m <= 29.7)  return '10';
    if (m >= 50.0 && m <= 54.0)  return '6';
    return '—';
}

function sMeterText(raw) {
    if (raw <= 0)   return 'S0';
    if (raw < 28)   return 'S1';
    if (raw < 42)   return 'S2';
    if (raw < 56)   return 'S3';
    if (raw < 70)   return 'S4';
    if (raw < 84)   return 'S5';
    if (raw < 98)   return 'S6';
    if (raw < 112)  return 'S7';
    if (raw < 128)  return 'S8';
    if (raw < 148)  return 'S9';
    if (raw < 168)  return 'S9+10';
    if (raw < 188)  return 'S9+20';
    if (raw < 218)  return 'S9+30';
    if (raw < 238)  return 'S9+40';
    return 'S9+50';
}

// ===== ACTIVE STATE HELPERS =====

function setActive(containerId, selector, value) {
    const container = document.getElementById(containerId) || document;
    container.querySelectorAll(selector).forEach(btn => {
        btn.classList.toggle('active', btn.dataset[Object.keys(btn.dataset)[0]] === value);
    });
}

function setBoolActive(id, on, cls = 'active-green') {
    const el = document.getElementById(id);
    if (el) {
        el.classList.remove('active', 'active-green', 'active-amber', 'active-red');
        if (on) el.classList.add(cls);
    }
}

// ===== RX ANTENNA (KMTronic) =====

function setRxAnt(n) {
    api('/api/rxant/' + n).then(() => updateRxAnt(n));
}

function updateRxAnt(n) {
    for (let i = 1; i <= 4; i++)
        document.getElementById('btn-rxant-' + i)?.classList.remove('active');
    document.getElementById('btn-rxant-' + n)?.classList.add('active');
}

window.setRxAnt = setRxAnt;

// ===== POLL: STATUS =====

let lastStatus = null;

async function pollStatus() {
    const data = await api('/api/status');
    if (!data || !data.connected) {
        dom.connDot.classList.remove('ok');
        dom.connText.textContent = 'DISCONNECTED';
        return;
    }

    dom.connDot.classList.add('ok');
    dom.connText.textContent = 'CONNECTED';
    lastStatus = data;

    dom.freq.innerHTML = formatFreq(data.freq);

    const band = data.band || freqToBand(data.freq);
    dom.badgeBand.textContent  = band !== '—' ? band + 'm' : '—';
    dom.badgeMode.textContent  = data.mode || '—';
    dom.badgeVfo.textContent   = data.vfo === 'B' ? 'VFO B' : 'VFO A';
    dom.powerWatts.textContent = data.power || '—';

    if (data.split) dom.badgeSplit.classList.remove('badge-hidden');
    else            dom.badgeSplit.classList.add('badge-hidden');

    if (data.tx) { dom.badgeTx.className = 'badge badge-tx'; dom.badgeTx.textContent = 'TX'; }
    else         { dom.badgeTx.className = 'badge badge-rx'; dom.badgeTx.textContent = 'RX'; }

    setActive('band-grid', '[data-band]', band);

    const modeLower = (data.mode || '').toLowerCase();
    document.querySelectorAll('#mode-grid [data-mode]').forEach(btn => {
        btn.classList.toggle('active', btn.dataset.mode === modeLower);
    });

    const pwr = data.power || 0;
    const pwrMap = { 5: 'qrp', 25: 'low', 50: 'mid', 100: 'high', 200: 'max' };
    const pwrKey = pwrMap[pwr] || '';
    document.querySelectorAll('#power-grid [data-power]').forEach(btn => {
        btn.classList.toggle('active', btn.dataset.power === pwrKey);
    });

    setBoolActive('btn-vfo-a', data.vfo !== 'B', 'active');
    setBoolActive('btn-vfo-b', data.vfo === 'B', 'active');
    setBoolActive('btn-split', data.split, 'active-amber');

    const pttBtn = document.getElementById('btn-ptt');
    if (pttBtn) pttBtn.classList.toggle('active', data.tx === true);

    // If radio says TX off but ptt.js thinks PTT is on, sync it
    if (!data.tx && window.pttState) window.stopPTT();
}

// ===== POLL: TOGGLE STATES (slow) =====

async function pollToggles() {
    const data = await api('/api/status/full');
    if (!data || data.connected === false) return;

    const ant = data.ant || 1;
    setBoolActive('btn-ant1', ant === 1, 'active');
    setBoolActive('btn-ant2', ant === 2, 'active');
    setBoolActive('btn-ant3', ant === 3, 'active');

    api('/api/rxant/get').then(d => { if (d?.rxant) updateRxAnt(d.rxant); });

    setBoolActive('btn-nb',    data.nb,    'active-green');
    setBoolActive('btn-nr',    data.nr,    'active-green');
    setBoolActive('btn-notch', data.notch, 'active-green');
    setBoolActive('btn-lock',  data.lock,  'active-amber');
    setBoolActive('btn-att',   data.att,   'active-green');
    setBoolActive('btn-vox',   data.vox,   'active-green');
    setBoolActive('btn-comp',  data.comp,  'active-green');
    setBoolActive('btn-mon',   data.mon,   'active-red');

    const preBtn = document.getElementById('btn-preamp');
    if (preBtn) {
        const pre = data.preamp || 0;
        preBtn.textContent = pre > 0 ? `PRE ${pre}` : 'PRE';
        setBoolActive('btn-preamp', pre > 0, 'active-green');
    }

    const agcBtn = document.getElementById('btn-agc');
    if (agcBtn) {
        const agc = data.agc || '?';
        agcBtn.textContent = `AGC ${agc}`;
        setBoolActive('btn-agc', agc !== 'OFF' && agc !== '?', 'active-green');
    }

    setBoolActive('btn-rit', data.rit, 'active');
    const ritHz = data.rit_offset || 0;
    dom.ritOff.textContent = (ritHz >= 0 ? '+' : '') + ritHz + ' Hz';

    setBoolActive('btn-xit', data.xit, 'active');
}

// ===== POLL: METERS =====

async function pollMeters() {
    const data = await api('/api/meters');
    if (!data || data.status !== 'ok') return;

    const sRaw = data.s_meter || 0;
    dom.sFill.style.width = Math.min(100, (sRaw / 255) * 100) + '%';
    dom.sVal.textContent  = sMeterText(sRaw);

    const pRaw = data.power || 0;
    dom.pwrFill.style.width = Math.min(100, (pRaw / 255) * 100) + '%';
    dom.pwrVal.textContent  = Math.round((pRaw / 255) * 200) + 'W';

    const swrRaw = data.swr || 0;
    const swrVal = 1.0 + (swrRaw / 255) * 4.0;
    dom.swrFill.style.width = Math.min(100, (swrRaw / 255) * 100) + '%';
    dom.swrVal.textContent  = swrVal.toFixed(1);
    dom.swrFill.className = 'meter-fill meter-fill-swr';
    if (swrVal > 3.0)      dom.swrFill.classList.add('danger');
    else if (swrVal > 2.0) dom.swrFill.classList.add('warn');

    const alcRaw = data.alc || 0;
    dom.alcFill.style.width = Math.min(100, (alcRaw / 255) * 100) + '%';
    dom.alcVal.textContent  = alcRaw;
}

// ===== POLL: RECORDING STATUS =====

async function pollRecording() {
    const data = await api('/api/record/status');
    if (!data) return;

    const recBtn = document.getElementById('btn-rec');
    if (data.recording) {
        dom.recDot.className = 'rec-dot on';
        if (recBtn) recBtn.classList.add('active-red');
    } else if (data.buffering) {
        dom.recDot.className = 'rec-dot buffer';
        if (recBtn) recBtn.classList.remove('active-red');
    } else {
        dom.recDot.className = 'rec-dot off';
        if (recBtn) recBtn.classList.remove('active-red');
    }
}

// ===== POLL: VOLUME & CW =====

async function pollKnobs() {
    const vol = await api('/api/volume/get');
    if (vol && vol.status === 'ok') {
        dom.volVal.textContent = vol.volume + '%';
        setBoolActive('btn-mute', vol.volume === 0, 'active-red');
    }

    const cw = await api('/api/cw-speed/get');
    if (cw && cw.status === 'ok') dom.cwVal.textContent = cw.wpm + ' WPM';
}

// ===== POLL: SESSION =====

async function pollSession() {
    const data = await api('/api/session');
    if (!data || data.status !== 'ok') return;
    dom.statTx.textContent   = data.tx_count  ?? '—';
    dom.statTime.textContent = data.tx_time   ?? '—';
    dom.statQsy.textContent  = data.qsy_count ?? '—';
}

// ===== POLL: DX CLUSTER =====

async function pollCluster() {
    const data = await api('/api/cluster/spots');
    if (!data || data.status !== 'ok' || !data.spots) return;

    dom.clusterBody.innerHTML = '';
    data.spots.forEach(spot => {
        const tr      = document.createElement('tr');
        const freqMhz = (spot.freq_khz || 0).toFixed(1);
        const timeStr = (spot.time || '').substring(11, 16);

        tr.innerHTML = `
            <td class="freq-col">${freqMhz}</td>
            <td class="call-col">${spot.dx_call || ''}</td>
            <td>${spot.spotter || ''}</td>
            <td>${timeStr}</td>
        `;

        tr.addEventListener('click', () => {
            if (spot.freq_hz)       api('/api/freq/set/' + spot.freq_hz);
            else if (spot.freq_khz) api('/api/freq/set/' + Math.round(spot.freq_khz * 1000));
        });

        dom.clusterBody.appendChild(tr);
    });
}

// ===== CLOCK =====

function updateClock() {
    dom.clock.textContent = new Date().toISOString().substring(11, 19) + 'z';
}

// ===== FREQUENCY ENTRY =====

function parseFreqInput(input) {
    input = input.trim().replace(/,/g, '');
    if (!input) return 0;

    if (input.includes('.')) {
        const val = parseFloat(input);
        if (isNaN(val)) return 0;
        if (val < 100)    return Math.round(val * 1000000);
        if (val < 100000) return Math.round(val * 1000);
        return Math.round(val);
    }

    const val = parseInt(input, 10);
    if (isNaN(val)) return 0;
    if (val < 100)    return val * 1000000;
    if (val < 100000) return val * 1000;
    return val;
}

function submitFreq() {
    const input = document.getElementById('freq-input');
    const hz    = parseFreqInput(input.value);
    if (hz > 0) { api('/api/freq/set/' + hz); input.value = ''; input.blur(); }
}
window.submitFreq = submitFreq;

document.getElementById('freq-input').addEventListener('keydown', (e) => {
    if (e.key === 'Enter')  submitFreq();
    if (e.key === 'Escape') { e.target.value = ''; e.target.blur(); }
});

// ===== BAND / MODE / POWER CLICK HANDLERS =====

document.querySelectorAll('#band-grid [data-band]').forEach(btn => {
    btn.addEventListener('click', () => api('/api/band/' + btn.dataset.band));
});

document.querySelectorAll('#mode-grid [data-mode]').forEach(btn => {
    btn.addEventListener('click', () => api('/api/mode/' + btn.dataset.mode));
});

document.querySelectorAll('#power-grid [data-power]').forEach(btn => {
    btn.addEventListener('click', () => api('/api/power/' + btn.dataset.power));
});

// ===== LOGOUT =====

document.getElementById('btn-logout').addEventListener('click', async () => {
    await fetch(API + '/api/auth/logout');
    window.location.href = '/login.html';
});

// ===== KEYBOARD SHORTCUTS =====

document.addEventListener('keydown', (e) => {
    if (e.target.tagName === 'INPUT' || e.target.tagName === 'TEXTAREA') return;
    switch (e.key.toUpperCase()) {
        case 'T':      window.togglePTT(); break;
        case 'R':      api('/api/record/toggle'); break;
        case 'L':      api('/api/toggle/lock'); break;
        case 'S':      api('/api/split/toggle'); break;
        case 'V':      api('/api/vfo/swap'); break;
        case 'ESCAPE': window.stopPTT(); break;
    }
});

// ===== STARTUP =====

async function init() {
    const authed = await checkAuth();
    if (!authed) return;

    await pollStatus();
    await pollMeters();
    await pollToggles();
    await pollKnobs();
    await pollRecording();
    pollCluster();
    pollSession();
    updateClock();

    dom.loading.classList.add('hidden');
    setTimeout(() => dom.loading.style.display = 'none', 300);

    setInterval(async () => { await pollStatus(); await pollMeters(); pollRecording(); }, POLL_FAST);
    setInterval(pollToggles, POLL_SLOW);
    setInterval(() => { pollKnobs(); pollSession(); }, POLL_SESSION);
    setInterval(pollCluster, POLL_CLUSTER);
    setInterval(updateClock, 1000);
}

init();

// ============================================================
//  FLEXKNOB — Web Serial bridge + /wsflexknob WebSocket
// ============================================================

(function () {
    const WS_PATH  = "/wsflexknob";
    const PING_MS  = 20000;

    let fkWs        = null;
    let fkPingTimer = null;
    let fkReconnect = null;

    const dot      = document.getElementById("fk-dot");
    const status   = document.getElementById("fk-status");
    const modeEl   = document.getElementById("fk-mode");
    const stepEl   = document.getElementById("fk-step");
    const actionEl = document.getElementById("fk-action");
    const btnConn  = document.getElementById("fk-btn-connect");
    const btnDisc  = document.getElementById("fk-btn-disconnect");

    function setDot(el, color) {
        el.style.background = `var(--${color})`;
        el.style.boxShadow  = `0 0 6px var(--${color})`;
    }

    function fkWsUrl() {
        const proto = location.protocol === "https:" ? "wss:" : "ws:";
        const token = new URLSearchParams(location.search).get("token");
        const base  = `${proto}//${location.host}${WS_PATH}`;
        return token ? `${base}?token=${encodeURIComponent(token)}` : base;
    }

    function fkWsConnect() {
        clearTimeout(fkReconnect);
        try { fkWs = new WebSocket(fkWsUrl()); } catch { schedFkReconnect(); return; }

        fkWs.onopen = () => {
            fkPingTimer = setInterval(() => {
                if (fkWs && fkWs.readyState === 1) fkWs.send(JSON.stringify({type:"ping"}));
            }, PING_MS);
        };

        fkWs.onmessage = (ev) => {
            try {
                const msg = JSON.parse(ev.data);
                if (msg.type === "state" || msg.type === "mode") {
                    if (modeEl)   modeEl.textContent = msg.mode || "—";
                    if (stepEl)   stepEl.textContent = msg.step || "—";
                }
                if (msg.type === "action" && actionEl) {
                    actionEl.textContent = msg.action || "";
                    clearTimeout(actionEl._t);
                    actionEl._t = setTimeout(() => { actionEl.textContent = ""; }, 2500);
                }
            } catch {}
        };

        fkWs.onerror = () => {};
        fkWs.onclose = () => { clearInterval(fkPingTimer); schedFkReconnect(); };
    }

    function schedFkReconnect() {
        clearTimeout(fkReconnect);
        fkReconnect = setTimeout(fkWsConnect, 3000);
    }

    function sendFkCmd(cmd) {
        if (fkWs && fkWs.readyState === 1)
            fkWs.send(JSON.stringify({type:"flexknob", cmd}));
    }

    fkWsConnect();

    // ── Web Serial ──────────────────────────────────────────────
    let serialPort   = null;
    let serialReader = null;
    let serialActive = false;
    let lineBuffer   = "";

    if (btnConn) btnConn.addEventListener("click", async () => {
        if (!("serial" in navigator)) {
            alert("Web Serial API not supported.\nUse Chrome or Edge on a desktop.");
            return;
        }
        try {
            const port = await navigator.serial.requestPort();
            const baud = parseInt(document.getElementById("fk-baud").value, 10);
            await openPort(port, baud);
        } catch(e) {
            setDot(dot, "red");
            if (status) status.textContent = "Failed: " + e.message;
        }
    });

    if (btnDisc) btnDisc.addEventListener("click", async () => {
        serialActive = false;
        try { if (serialReader) { await serialReader.cancel(); serialReader = null; } } catch {}
        try { if (serialPort)  { await serialPort.close();   serialPort  = null; } } catch {}
        setDot(dot, "red");
        if (status) status.textContent = "Not connected";
        if (btnConn) { btnConn.disabled = false; btnConn.style.opacity = "1"; }
        if (btnDisc) { btnDisc.disabled = true;  btnDisc.style.opacity = "0.4"; }
    });

    async function readSerialLoop() {
        const decoder = new TextDecoderStream();
        serialPort.readable.pipeTo(decoder.writable);
        serialReader = decoder.readable.getReader();
        try {
            while (serialActive) {
                const { value, done } = await serialReader.read();
                if (done) break;
                lineBuffer += value;
                const delim = lineBuffer.includes(";") ? ";" : "\n";
                const parts = lineBuffer.split(delim);
                lineBuffer  = parts.pop();
                for (const part of parts) {
                    const cmd = part.trim().toUpperCase();
                    if (cmd) sendFkCmd(cmd);
                }
            }
        } catch(e) {
            if (serialActive) {
                setDot(dot, "red");
                if (status) status.textContent = "Read error: " + e.message;
                serialActive = false;
                if (btnConn) { btnConn.disabled = false; btnConn.style.opacity = "1"; }
                if (btnDisc) { btnDisc.disabled = true;  btnDisc.style.opacity = "0.4"; }
            }
        }
    }

    async function openPort(port, baud) {
        serialPort = port;
        await serialPort.open({ baudRate: baud, dataBits: 8, stopBits: 1, parity: "none", flowControl: "none" });
        serialActive = true;
        localStorage.setItem("fk_baud", baud);
        setDot(dot, "green");
        if (status) status.textContent = `Connected at ${baud} baud`;
        if (btnConn) { btnConn.disabled = true;  btnConn.style.opacity = "0.4"; }
        if (btnDisc) { btnDisc.disabled = false; btnDisc.style.opacity = "1"; }
        const baudSel = document.getElementById("fk-baud");
        if (baudSel) baudSel.value = String(baud);
        readSerialLoop();
    }

    if ("serial" in navigator) {
        navigator.serial.getPorts().then(ports => {
            if (ports.length === 1 && !serialActive) {
                const savedBaud = parseInt(localStorage.getItem("fk_baud") || "115200", 10);
                if (status) status.textContent = "Auto-connecting…";
                openPort(ports[0], savedBaud).catch(() => {
                    if (status) status.textContent = "Auto-connect failed — click Connect";
                    setDot(dot, "red");
                });
            }
        });
    }
})();
