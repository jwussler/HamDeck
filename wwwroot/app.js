// === HamDeck v3 — Web Control Dashboard ===
// Full rig control with session authentication.
// Polls the HamDeck API and sends commands via the same REST endpoints
// that Stream Deck uses, ensuring GUI/API/Web stay in sync.

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
        isAdmin = data.is_admin;

        // Show admin link if admin
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

// Make api() available to inline onclick handlers
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
    // Meters
    sFill:   $('s-fill'),    sVal:   $('s-val'),
    pwrFill: $('pwr-fill'),  pwrVal: $('pwr-val'),
    swrFill: $('swr-fill'),  swrVal: $('swr-val'),
    alcFill: $('alc-fill'),  alcVal: $('alc-val'),
    // Sliders
    volVal:  $('vol-val'),
    cwVal:   $('cw-val'),
    ritOff:  $('rit-offset'),
    // Stats
    statTx:  $('stat-tx'),
    statTime:$('stat-time'),
    statQsy: $('stat-qsy'),
    // Cluster
    clusterBody: $('cluster-body'),
    // Recording
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
    if (m >= 1.8 && m <= 2.0) return '160';
    if (m >= 3.5 && m <= 4.0) return '80';
    if (m >= 5.3 && m <= 5.4) return '60';
    if (m >= 7.0 && m <= 7.3) return '40';
    if (m >= 10.1 && m <= 10.15) return '30';
    if (m >= 14.0 && m <= 14.35) return '20';
    if (m >= 18.068 && m <= 18.168) return '17';
    if (m >= 21.0 && m <= 21.45) return '15';
    if (m >= 24.89 && m <= 24.99) return '12';
    if (m >= 28.0 && m <= 29.7) return '10';
    if (m >= 50.0 && m <= 54.0) return '6';
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

// ===== POLL: STATUS =====

let lastStatus = null;

async function pollStatus() {
    const data = await api('/api/status');
    if (!data) {
        dom.connDot.classList.remove('ok');
        dom.connText.textContent = 'DISCONNECTED';
        return;
    }

    if (!data.connected) {
        dom.connDot.classList.remove('ok');
        dom.connText.textContent = 'DISCONNECTED';
        return;
    }

    dom.connDot.classList.add('ok');
    dom.connText.textContent = 'CONNECTED';
    lastStatus = data;

    // Frequency
    dom.freq.innerHTML = formatFreq(data.freq);

    // Badges
    const band = data.band || freqToBand(data.freq);
    dom.badgeBand.textContent = band !== '—' ? band + 'm' : '—';
    dom.badgeMode.textContent = data.mode || '—';
    dom.badgeVfo.textContent = data.vfo === 'B' ? 'VFO B' : 'VFO A';
    dom.powerWatts.textContent = data.power || '—';

    // Split badge
    if (data.split) {
        dom.badgeSplit.classList.remove('badge-hidden');
    } else {
        dom.badgeSplit.classList.add('badge-hidden');
    }

    // TX badge
    if (data.tx) {
        dom.badgeTx.className = 'badge badge-tx';
        dom.badgeTx.textContent = 'TX';
    } else {
        dom.badgeTx.className = 'badge badge-rx';
        dom.badgeTx.textContent = 'RX';
    }

    // Active band button
    setActive('band-grid', '[data-band]', band);

    // Active mode button
    const modeLower = (data.mode || '').toLowerCase();
    document.querySelectorAll('#mode-grid [data-mode]').forEach(btn => {
        btn.classList.toggle('active', btn.dataset.mode === modeLower);
    });

    // Power preset highlighting
    const pwr = data.power || 0;
    const pwrMap = { 5: 'qrp', 25: 'low', 50: 'mid', 100: 'high', 200: 'max' };
    const pwrKey = pwrMap[pwr] || '';
    document.querySelectorAll('#power-grid [data-power]').forEach(btn => {
        btn.classList.toggle('active', btn.dataset.power === pwrKey);
    });

    // VFO buttons
    setBoolActive('btn-vfo-a', data.vfo !== 'B', 'active');
    setBoolActive('btn-vfo-b', data.vfo === 'B', 'active');
    setBoolActive('btn-split', data.split, 'active-amber');

    // PTT — sync with radio state
    const pttBtn = document.getElementById('btn-ptt');
    if (pttBtn) {
        pttBtn.classList.toggle('active', data.tx === true);
    }
    // If radio says TX off but we think PTT is on, sync it
    if (!data.tx && pttState) {
        stopPTT();
    }
}

// ===== POLL: TOGGLE STATES (slow) =====

async function pollToggles() {
    const data = await api('/api/status/full');
    if (!data || data.connected === false) return;

    // Antenna
    const ant = data.ant || 1;
    setBoolActive('btn-ant1', ant === 1, 'active');
    setBoolActive('btn-ant2', ant === 2, 'active');
    setBoolActive('btn-ant3', ant === 3, 'active');
    setBoolActive('btn-rxant', data.rxant, 'active-green');

    // Filters / toggles
    setBoolActive('btn-nb', data.nb, 'active-green');
    setBoolActive('btn-nr', data.nr, 'active-green');
    setBoolActive('btn-notch', data.notch, 'active-green');
    setBoolActive('btn-lock', data.lock, 'active-amber');
    setBoolActive('btn-att', data.att, 'active-green');
    setBoolActive('btn-vox', data.vox, 'active-green');
    setBoolActive('btn-comp', data.comp, 'active-green');

    // Preamp — show level
    const preBtn = document.getElementById('btn-preamp');
    if (preBtn) {
        const pre = data.preamp || 0;
        preBtn.textContent = pre > 0 ? `PRE ${pre}` : 'PRE';
        setBoolActive('btn-preamp', pre > 0, 'active-green');
    }

    // AGC — show mode
    const agcBtn = document.getElementById('btn-agc');
    if (agcBtn) {
        const agc = data.agc || '?';
        agcBtn.textContent = `AGC ${agc}`;
        setBoolActive('btn-agc', agc !== 'OFF' && agc !== '?', 'active-green');
    }

    // RIT
    setBoolActive('btn-rit', data.rit, 'active');
    const ritHz = data.rit_offset || 0;
    dom.ritOff.textContent = (ritHz >= 0 ? '+' : '') + ritHz + ' Hz';

    // XIT
    setBoolActive('btn-xit', data.xit, 'active');
}

// ===== POLL: METERS =====

async function pollMeters() {
    const data = await api('/api/meters');
    if (!data || data.status !== 'ok') return;

    const sRaw = data.s_meter || 0;
    dom.sFill.style.width = Math.min(100, (sRaw / 255) * 100) + '%';
    dom.sVal.textContent = sMeterText(sRaw);

    const pRaw = data.power || 0;
    dom.pwrFill.style.width = Math.min(100, (pRaw / 255) * 100) + '%';
    dom.pwrVal.textContent = Math.round((pRaw / 255) * 200) + 'W';

    const swrRaw = data.swr || 0;
    const swrVal = 1.0 + (swrRaw / 255) * 4.0;
    dom.swrFill.style.width = Math.min(100, (swrRaw / 255) * 100) + '%';
    dom.swrVal.textContent = swrVal.toFixed(1);
    dom.swrFill.className = 'meter-fill meter-fill-swr';
    if (swrVal > 3.0) dom.swrFill.classList.add('danger');
    else if (swrVal > 2.0) dom.swrFill.classList.add('warn');

    const alcRaw = data.alc || 0;
    dom.alcFill.style.width = Math.min(100, (alcRaw / 255) * 100) + '%';
    dom.alcVal.textContent = alcRaw;
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
    if (cw && cw.status === 'ok') {
        dom.cwVal.textContent = cw.wpm + ' WPM';
    }
}

// ===== POLL: SESSION =====

async function pollSession() {
    const data = await api('/api/session');
    if (!data || data.status !== 'ok') return;
    dom.statTx.textContent = data.tx_count ?? '—';
    dom.statTime.textContent = data.tx_time ?? '—';
    dom.statQsy.textContent = data.qsy_count ?? '—';
}

// ===== POLL: DX CLUSTER =====

async function pollCluster() {
    const data = await api('/api/cluster/spots');
    if (!data || data.status !== 'ok' || !data.spots) return;

    dom.clusterBody.innerHTML = '';
    data.spots.forEach(spot => {
        const tr = document.createElement('tr');
        const freqMhz = (spot.freq_khz || 0).toFixed(1);
        const time = spot.time || '';
        const timeStr = time ? time.substring(11, 16) : '';

        tr.innerHTML = `
            <td class="freq-col">${freqMhz}</td>
            <td class="call-col">${spot.dx_call || ''}</td>
            <td>${spot.spotter || ''}</td>
            <td>${timeStr}</td>
        `;

        // Click-to-tune: set frequency and mode from spot
        tr.addEventListener('click', () => {
            if (spot.freq_hz) {
                api('/api/freq/set/' + spot.freq_hz);
            } else if (spot.freq_khz) {
                api('/api/freq/set/' + Math.round(spot.freq_khz * 1000));
            }
        });

        dom.clusterBody.appendChild(tr);
    });
}

// ===== CLOCK =====

function updateClock() {
    const now = new Date();
    const utc = now.toISOString().substring(11, 19);
    dom.clock.textContent = utc + 'z';
}

// ===== FREQUENCY ENTRY =====

function parseFreqInput(input) {
    input = input.trim().replace(/,/g, '');
    if (!input) return 0;

    if (input.includes('.')) {
        const val = parseFloat(input);
        if (isNaN(val)) return 0;
        // If < 100, treat as MHz
        if (val < 100) return Math.round(val * 1000000);
        // If < 100000, treat as kHz
        if (val < 100000) return Math.round(val * 1000);
        return Math.round(val);
    }

    const val = parseInt(input, 10);
    if (isNaN(val)) return 0;
    if (val < 100) return val * 1000000;    // e.g. "14" → 14 MHz
    if (val < 100000) return val * 1000;    // e.g. "14200" → 14200 kHz
    return val;                              // e.g. "14200000" → Hz
}

function submitFreq() {
    const input = document.getElementById('freq-input');
    const hz = parseFreqInput(input.value);
    if (hz > 0) {
        api('/api/freq/set/' + hz);
        input.value = '';
        input.blur();
    }
}
window.submitFreq = submitFreq;

document.getElementById('freq-input').addEventListener('keydown', (e) => {
    if (e.key === 'Enter') submitFreq();
    if (e.key === 'Escape') { e.target.value = ''; e.target.blur(); }
});

// ===== PTT WITH 3-MINUTE TIMEOUT =====

const PTT_TIMEOUT_SECONDS = 180; // 3 minutes
let pttState = false;
let pttTimer = null;
let pttStartTime = null;
let pttCountdownInterval = null;

function togglePTT() {
    if (pttState) {
        stopPTT();
    } else {
        startPTT();
    }
}
window.togglePTT = togglePTT;

function startPTT() {
    pttState = true;
    pttStartTime = Date.now();
    api('/api/ptt/on');

    // Show countdown
    const timerEl = document.getElementById('ptt-timer');
    const countdownEl = document.getElementById('ptt-countdown');
    if (timerEl) timerEl.style.display = 'block';

    // Update countdown every second
    pttCountdownInterval = setInterval(() => {
        if (!pttState) return;
        const elapsed = Math.floor((Date.now() - pttStartTime) / 1000);
        const remaining = PTT_TIMEOUT_SECONDS - elapsed;
        if (remaining <= 0) {
            stopPTT();
            return;
        }
        const min = Math.floor(remaining / 60);
        const sec = remaining % 60;
        if (countdownEl) countdownEl.textContent = min + ':' + String(sec).padStart(2, '0');
    }, 1000);

    // Hard timeout safety
    pttTimer = setTimeout(() => {
        Logger_warn('PTT timeout reached (3 min)');
        stopPTT();
    }, PTT_TIMEOUT_SECONDS * 1000);
}

function stopPTT() {
    pttState = false;
    api('/api/ptt/off');

    if (pttTimer) { clearTimeout(pttTimer); pttTimer = null; }
    if (pttCountdownInterval) { clearInterval(pttCountdownInterval); pttCountdownInterval = null; }
    pttStartTime = null;

    const timerEl = document.getElementById('ptt-timer');
    if (timerEl) timerEl.style.display = 'none';

    // If mic is active and PTT timed out, stop mic too (safety)
    // This ensures the radio goes back to MIC source
}

function Logger_warn(msg) { console.warn('[HamDeck]', msg); }

// ===== AUDIO STREAMING =====

let audioCtx = null;
let audioWs = null;
let audioPlaying = false;
let rxGainNode = null;

function setRxVolume(val) {
    if (rxGainNode) rxGainNode.gain.value = parseFloat(val);
    const label = document.getElementById('rx-vol-label');
    if (label) label.textContent = val + 'x';
}
window.setRxVolume = setRxVolume;

// Radio SSB OUT LEVEL slider
(function initRadioVolSlider() {
    const slider = document.getElementById('rx-radio-vol');
    const label = document.getElementById('rx-radio-label');
    if (!slider) return;

    // Load current value from radio
    api('/api/ssb-out-level/get').then(data => {
        if (data && data.status === 'ok') {
            slider.value = data.level;
            if (label) label.textContent = data.level;
        }
    });

    let debounce = null;
    slider.addEventListener('input', () => {
        if (label) label.textContent = slider.value;
        // Debounce to avoid flooding the serial port
        clearTimeout(debounce);
        debounce = setTimeout(() => {
            api('/api/ssb-out-level/set/' + slider.value);
        }, 200);
    });
})();

function toggleAudio() {
    if (audioPlaying) {
        stopAudio();
    } else {
        startAudio();
    }
}
window.toggleAudio = toggleAudio;

function startAudio() {
    const btn = document.getElementById('btn-audio');
    const statusEl = document.getElementById('audio-status');

    try {
        audioCtx = new (window.AudioContext || window.webkitAudioContext)({ sampleRate: 22050 });
        const wsProto = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
        audioWs = new WebSocket(`${wsProto}//${window.location.host}/ws`);
        audioWs.binaryType = 'arraybuffer';

        // Adaptive jitter buffer state
        let nextPlayTime = 0;
        let bufferTarget = 0.3; // Start with 300ms buffer
        const BUFFER_MIN = 0.15;
        const BUFFER_MAX = 2.0;
        let lastArrival = 0;
        let jitterSamples = [];
        const JITTER_WINDOW = 20; // Track last 20 packets

        audioWs.onopen = () => {
            audioPlaying = true;
            nextPlayTime = 0;
            btn.textContent = '■ STOP';
            btn.classList.add('playing');
            statusEl.textContent = 'Buffering...';

            // Create persistent gain node from slider value
            const vol = parseFloat(document.getElementById('rx-volume')?.value || '3');
            rxGainNode = audioCtx.createGain();
            rxGainNode.gain.value = vol;
            rxGainNode.connect(audioCtx.destination);
        };

        audioWs.onmessage = (event) => {
            if (!audioCtx || audioCtx.state === 'closed' || !rxGainNode) return;
            const pcm16 = new Int16Array(event.data);
            if (pcm16.length === 0) return;

            // Track arrival jitter
            const now = performance.now();
            if (lastArrival > 0) {
                const gap = now - lastArrival;
                jitterSamples.push(gap);
                if (jitterSamples.length > JITTER_WINDOW) jitterSamples.shift();

                // Calculate jitter (standard deviation of inter-arrival times)
                if (jitterSamples.length >= 5) {
                    const avg = jitterSamples.reduce((a, b) => a + b, 0) / jitterSamples.length;
                    const variance = jitterSamples.reduce((sum, v) => sum + (v - avg) ** 2, 0) / jitterSamples.length;
                    const jitterMs = Math.sqrt(variance);

                    // Adapt buffer: target = 2x jitter, clamped
                    const newTarget = Math.max(BUFFER_MIN, Math.min(BUFFER_MAX, (jitterMs * 3) / 1000));
                    // Smooth adjustment
                    bufferTarget = bufferTarget * 0.9 + newTarget * 0.1;
                }
            }
            lastArrival = now;

            // Convert PCM16 to Float32
            const float32 = new Float32Array(pcm16.length);
            for (let i = 0; i < pcm16.length; i++) {
                float32[i] = pcm16[i] / 32768.0;
            }

            const buffer = audioCtx.createBuffer(1, float32.length, audioCtx.sampleRate);
            buffer.getChannelData(0).set(float32);
            const source = audioCtx.createBufferSource();
            source.buffer = buffer;
            source.connect(rxGainNode);

            const chunkDuration = float32.length / audioCtx.sampleRate;
            const currentTime = audioCtx.currentTime;

            // Schedule playback with jitter buffer
            if (nextPlayTime < currentTime) {
                // Buffer underrun — reset with current buffer target
                nextPlayTime = currentTime + bufferTarget;
            }

            source.start(nextPlayTime);
            nextPlayTime += chunkDuration;

            // Update status with buffer info
            const bufferedMs = Math.round((nextPlayTime - currentTime) * 1000);
            const targetMs = Math.round(bufferTarget * 1000);
            statusEl.textContent = `Streaming (buf: ${bufferedMs}ms / target: ${targetMs}ms)`;
        };

        audioWs.onclose = () => stopAudio();
        audioWs.onerror = () => stopAudio();
    } catch (e) {
        statusEl.textContent = 'Audio not available';
    }
}

function stopAudio() {
    const btn = document.getElementById('btn-audio');
    const statusEl = document.getElementById('audio-status');

    if (audioWs) { audioWs.close(); audioWs = null; }
    if (audioCtx) { audioCtx.close().catch(() => {}); audioCtx = null; }
    audioPlaying = false;
    rxGainNode = null;
    if (btn) { btn.textContent = '▶ LISTEN'; btn.classList.remove('playing'); }
    if (statusEl) statusEl.textContent = 'Off';
}

// ===== TX AUDIO (MICROPHONE → RADIO) =====

let micStream = null;
let micCtx = null;
let micWs = null;
let micProcessor = null;
let micActive = false;

function toggleMic() {
    if (micActive) {
        stopMic();
    } else {
        startMic();
    }
}
window.toggleMic = toggleMic;

async function startMic() {
    const btn = document.getElementById('btn-mic');
    const statusEl = document.getElementById('mic-status');
    const levelTrack = document.getElementById('mic-level-track');

    try {
        // Switch radio to USB audio source FIRST
        if (statusEl) statusEl.textContent = 'Switching to USB audio...';
        await api('/api/remote-tx/on');

        // Request microphone access
        micStream = await navigator.mediaDevices.getUserMedia({
            audio: {
                channelCount: 1,
                sampleRate: 48000,
                echoCancellation: false,
                noiseSuppression: false,
                autoGainControl: false
            }
        });

        // Create audio context at 48kHz to match the radio
        micCtx = new (window.AudioContext || window.webkitAudioContext)({ sampleRate: 48000 });
        const source = micCtx.createMediaStreamSource(micStream);

        // Connect WebSocket for TX audio (pass token for Cloudflare compatibility)
        const wsProto = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
        const tokenParam = sessionToken ? `?token=${sessionToken}` : '';
        micWs = new WebSocket(`${wsProto}//${window.location.host}/ws/tx${tokenParam}`);
        micWs.binaryType = 'arraybuffer';

        micWs.onopen = () => {
            micActive = true;
            if (btn) { btn.textContent = '🎤 STOP'; btn.classList.add('mic-on'); }
            if (statusEl) statusEl.textContent = 'Streaming to radio';
            if (levelTrack) levelTrack.style.display = 'block';

            // Use ScriptProcessorNode to capture PCM
            // (AudioWorklet would be better but requires HTTPS)
            micProcessor = micCtx.createScriptProcessor(4096, 1, 1);
            const levelFill = document.getElementById('mic-level-fill');

            micProcessor.onaudioprocess = (e) => {
                if (!micWs || micWs.readyState !== WebSocket.OPEN) return;

                const float32 = e.inputBuffer.getChannelData(0);

                // Update level meter
                if (levelFill) {
                    let peak = 0;
                    for (let i = 0; i < float32.length; i++) {
                        const abs = Math.abs(float32[i]);
                        if (abs > peak) peak = abs;
                    }
                    levelFill.style.width = Math.min(100, peak * 200) + '%';
                }

                // Convert Float32 → Int16 PCM
                const pcm16 = new Int16Array(float32.length);
                for (let i = 0; i < float32.length; i++) {
                    const s = Math.max(-1, Math.min(1, float32[i]));
                    pcm16[i] = s < 0 ? s * 0x8000 : s * 0x7FFF;
                }

                micWs.send(pcm16.buffer);
            };

            source.connect(micProcessor);
            micProcessor.connect(micCtx.destination);
        };

        micWs.onclose = (e) => {
            if (e.reason) {
                const statusEl = document.getElementById('mic-status');
                if (statusEl) statusEl.textContent = e.reason;
                // Keep the message visible for a moment before resetting
                setTimeout(() => stopMic(), 2000);
            } else {
                stopMic();
            }
        };
        micWs.onerror = () => {
            if (statusEl) statusEl.textContent = 'Connection failed';
            stopMic();
        };

    } catch (e) {
        console.error('Mic error:', e);
        if (statusEl) statusEl.textContent = e.name === 'NotAllowedError' ? 'Mic blocked' : 'Mic error';
        stopMic();
    }
}

function stopMic() {
    if (!micActive && !micWs && !micStream) return; // Already stopped

    const btn = document.getElementById('btn-mic');
    const statusEl = document.getElementById('mic-status');
    const levelTrack = document.getElementById('mic-level-track');
    const levelFill = document.getElementById('mic-level-fill');

    micActive = false;

    if (micProcessor) { micProcessor.disconnect(); micProcessor = null; }
    if (micCtx) { micCtx.close().catch(() => {}); micCtx = null; }
    if (micWs) { micWs.close(); micWs = null; }
    if (micStream) { micStream.getTracks().forEach(t => t.stop()); micStream = null; }

    // Switch radio back to front panel MIC
    api('/api/remote-tx/off');

    if (btn) { btn.textContent = '🎤 MIC'; btn.classList.remove('mic-on'); }
    if (statusEl) statusEl.textContent = 'Off';
    if (levelTrack) levelTrack.style.display = 'none';
    if (levelFill) levelFill.style.width = '0%';
}

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
    // Don't capture if user is typing in an input
    if (e.target.tagName === 'INPUT' || e.target.tagName === 'TEXTAREA') return;

    switch (e.key.toUpperCase()) {
        case 'T': togglePTT(); break;
        case 'R': api('/api/record/toggle'); break;
        case 'L': api('/api/toggle/lock'); break;
        case 'S': api('/api/split/toggle'); break;
        case 'V': api('/api/vfo/swap'); break;
        case 'ESCAPE': stopPTT(); break;
    }
});

// ===== STARTUP =====

async function init() {
    const authed = await checkAuth();
    if (!authed) return;

    // Initial polls
    await pollStatus();
    await pollMeters();
    await pollToggles();
    await pollKnobs();
    await pollRecording();
    pollCluster();
    pollSession();
    updateClock();

    // Hide loading
    dom.loading.classList.add('hidden');
    setTimeout(() => dom.loading.style.display = 'none', 300);

    // Fast poll: freq, mode, tx, meters (500ms)
    setInterval(async () => {
        await pollStatus();
        await pollMeters();
        pollRecording();
    }, POLL_FAST);

    // Slow poll: toggles, filters, antenna (2.5s)
    setInterval(pollToggles, POLL_SLOW);

    setInterval(() => {
        pollKnobs();
        pollSession();
    }, POLL_SESSION);

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
        el.style.background  = `var(--${color})`;
        el.style.boxShadow   = `0 0 6px var(--${color})`;
    }

    // ── WebSocket to server ───────────────────────────────────
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
                    if (modeEl) modeEl.textContent = msg.mode || "—";
                    if (stepEl) stepEl.textContent = msg.step || "—";
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

    // ── Web Serial ────────────────────────────────────────────
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
        try { if (serialPort) { await serialPort.close(); serialPort = null; } } catch {}
        setDot(dot, "red");
        if (status) status.textContent = "Not connected";
        btnConn.disabled = false;
        btnConn.style.opacity = "1";
        btnDisc.disabled = true;
        btnDisc.style.opacity = "0.4";
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
                btnConn.disabled = false;
                btnConn.style.opacity = "1";
                btnDisc.disabled = true;
                btnDisc.style.opacity = "0.4";
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
        if (btnDisc) { btnDisc.disabled = false;  btnDisc.style.opacity = "1";  }
        const baudSel = document.getElementById("fk-baud");
        if (baudSel) baudSel.value = String(baud);
        readSerialLoop();
    }

    // Auto-reconnect on page load if browser already has a granted port
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
