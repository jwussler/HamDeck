// === HamDeck — Audio subsystem ===
// RX streaming (radio → browser) and TX mic (browser → radio).
// Loaded before app.js. Exports to window: toggleAudio, startAudio, stopAudio,
//   toggleMic, startMic, stopMic, setRxVolume.
// Reads window.api() and window.sessionToken (set by app.js at init time).

// ── RX Audio ────────────────────────────────────────────────────────────────

let audioCtx    = null;
let audioWs     = null;
let audioPlaying = false;
let rxGainNode  = null;

function setRxVolume(val) {
    if (rxGainNode) rxGainNode.gain.value = parseFloat(val);
    const label = document.getElementById('rx-vol-label');
    if (label) label.textContent = val + 'x';
}

// Radio SSB OUT LEVEL slider — loads current value from radio on page load
(function initRadioVolSlider() {
    const slider = document.getElementById('rx-radio-vol');
    const label  = document.getElementById('rx-radio-label');
    if (!slider) return;

    window.api('/api/ssb-out-level/get').then(data => {
        if (data && data.status === 'ok') {
            slider.value = data.level;
            if (label) label.textContent = data.level;
        }
    });

    let debounce = null;
    slider.addEventListener('input', () => {
        if (label) label.textContent = slider.value;
        clearTimeout(debounce);
        debounce = setTimeout(() => window.api('/api/ssb-out-level/set/' + slider.value), 200);
    });
})();

function toggleAudio() {
    if (audioPlaying) stopAudio(); else startAudio();
}

function startAudio() {
    const btn      = document.getElementById('btn-audio');
    const statusEl = document.getElementById('audio-status');

    // Kill monitor to prevent TX audio feedback loop when listening remotely
    window.api('/api/mon/off');

    try {
        audioCtx = new (window.AudioContext || window.webkitAudioContext)({ sampleRate: 22050 });
        const wsProto = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
        audioWs = new WebSocket(`${wsProto}//${window.location.host}/ws`);
        audioWs.binaryType = 'arraybuffer';

        // Adaptive jitter buffer
        let nextPlayTime  = 0;
        let bufferTarget  = 0.3;
        const BUFFER_MIN  = 0.15;
        const BUFFER_MAX  = 2.0;
        let lastArrival   = 0;
        let jitterSamples = [];
        const JITTER_WINDOW = 20;

        audioWs.onopen = () => {
            audioPlaying = true;
            nextPlayTime = 0;
            if (btn) { btn.textContent = '■ STOP'; btn.classList.add('playing'); }
            if (statusEl) statusEl.textContent = 'Buffering...';

            const vol = parseFloat(document.getElementById('rx-volume')?.value || '3');
            rxGainNode = audioCtx.createGain();
            rxGainNode.gain.value = vol;
            rxGainNode.connect(audioCtx.destination);
        };

        audioWs.onmessage = (event) => {
            if (!audioCtx || audioCtx.state === 'closed' || !rxGainNode) return;
            const pcm16 = new Int16Array(event.data);
            if (pcm16.length === 0) return;

            const now = performance.now();
            if (lastArrival > 0) {
                const gap = now - lastArrival;
                jitterSamples.push(gap);
                if (jitterSamples.length > JITTER_WINDOW) jitterSamples.shift();
                if (jitterSamples.length >= 5) {
                    const avg      = jitterSamples.reduce((a, b) => a + b, 0) / jitterSamples.length;
                    const variance = jitterSamples.reduce((s, v) => s + (v - avg) ** 2, 0) / jitterSamples.length;
                    const jitterMs = Math.sqrt(variance);
                    const newTarget = Math.max(BUFFER_MIN, Math.min(BUFFER_MAX, (jitterMs * 3) / 1000));
                    bufferTarget = bufferTarget * 0.9 + newTarget * 0.1;
                }
            }
            lastArrival = now;

            const float32 = new Float32Array(pcm16.length);
            for (let i = 0; i < pcm16.length; i++) float32[i] = pcm16[i] / 32768.0;

            const buffer = audioCtx.createBuffer(1, float32.length, audioCtx.sampleRate);
            buffer.getChannelData(0).set(float32);
            const source = audioCtx.createBufferSource();
            source.buffer = buffer;
            source.connect(rxGainNode);

            const chunkDuration = float32.length / audioCtx.sampleRate;
            const currentTime   = audioCtx.currentTime;
            if (nextPlayTime < currentTime) nextPlayTime = currentTime + bufferTarget;
            source.start(nextPlayTime);
            nextPlayTime += chunkDuration;

            const bufferedMs = Math.round((nextPlayTime - currentTime) * 1000);
            const targetMs   = Math.round(bufferTarget * 1000);
            if (statusEl) statusEl.textContent = `Streaming (buf: ${bufferedMs}ms / target: ${targetMs}ms)`;
        };

        audioWs.onclose = () => stopAudio();
        audioWs.onerror = () => stopAudio();
    } catch (e) {
        if (statusEl) statusEl.textContent = 'Audio not available';
    }
}

function stopAudio() {
    const btn      = document.getElementById('btn-audio');
    const statusEl = document.getElementById('audio-status');
    if (audioWs)  { audioWs.close();             audioWs  = null; }
    if (audioCtx) { audioCtx.close().catch(() => {}); audioCtx = null; }
    audioPlaying = false;
    rxGainNode   = null;
    if (btn)      { btn.textContent = '▶ LISTEN'; btn.classList.remove('playing'); }
    if (statusEl) statusEl.textContent = 'Off';
}

// ── TX Audio (browser mic → radio) ──────────────────────────────────────────

let micStream    = null;
let micCtx       = null;
let micWs        = null;
let micProcessor = null;
let micActive    = false;

function toggleMic() {
    if (micActive) stopMic(); else startMic();
}

async function startMic() {
    const btn        = document.getElementById('btn-mic');
    const statusEl   = document.getElementById('mic-status');
    const levelTrack = document.getElementById('mic-level-track');

    try {
        if (statusEl) statusEl.textContent = 'Switching to USB audio...';
        await window.api('/api/remote-tx/on');

        micStream = await navigator.mediaDevices.getUserMedia({
            audio: { channelCount: 1, sampleRate: 48000,
                     echoCancellation: false, noiseSuppression: false, autoGainControl: false }
        });

        micCtx = new (window.AudioContext || window.webkitAudioContext)({ sampleRate: 48000 });
        const source  = micCtx.createMediaStreamSource(micStream);
        const wsProto = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
        const token   = window.sessionToken;
        const tokenParam = token ? `?token=${token}` : '';
        micWs = new WebSocket(`${wsProto}//${window.location.host}/ws/tx${tokenParam}`);
        micWs.binaryType = 'arraybuffer';

        micWs.onopen = () => {
            micActive = true;
            if (btn)        { btn.textContent = '🎤 STOP'; btn.classList.add('mic-on'); }
            if (statusEl)   statusEl.textContent = 'Streaming to radio';
            if (levelTrack) levelTrack.style.display = 'block';

            micProcessor = micCtx.createScriptProcessor(4096, 1, 1);
            const levelFill = document.getElementById('mic-level-fill');

            micProcessor.onaudioprocess = (e) => {
                if (!micWs || micWs.readyState !== WebSocket.OPEN) return;
                const float32 = e.inputBuffer.getChannelData(0);

                if (levelFill) {
                    let peak = 0;
                    for (let i = 0; i < float32.length; i++) { const a = Math.abs(float32[i]); if (a > peak) peak = a; }
                    levelFill.style.width = Math.min(100, peak * 200) + '%';
                }

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
                const el = document.getElementById('mic-status');
                if (el) el.textContent = e.reason;
                setTimeout(() => stopMic(), 2000);
            } else {
                stopMic();
            }
        };
        micWs.onerror = () => { if (statusEl) statusEl.textContent = 'Connection failed'; stopMic(); };

    } catch (e) {
        console.error('Mic error:', e);
        if (statusEl) statusEl.textContent = e.name === 'NotAllowedError' ? 'Mic blocked' : 'Mic error';
        stopMic();
    }
}

function stopMic() {
    if (!micActive && !micWs && !micStream) return;

    const btn        = document.getElementById('btn-mic');
    const statusEl   = document.getElementById('mic-status');
    const levelTrack = document.getElementById('mic-level-track');
    const levelFill  = document.getElementById('mic-level-fill');

    micActive = false;
    if (micProcessor) { micProcessor.disconnect(); micProcessor = null; }
    if (micCtx)       { micCtx.close().catch(() => {}); micCtx = null; }
    if (micWs)        { micWs.close();  micWs = null; }
    if (micStream)    { micStream.getTracks().forEach(t => t.stop()); micStream = null; }

    window.api('/api/remote-tx/off');

    if (btn)        { btn.textContent = '🎤 MIC'; btn.classList.remove('mic-on'); }
    if (statusEl)   statusEl.textContent = 'Off';
    if (levelTrack) levelTrack.style.display = 'none';
    if (levelFill)  levelFill.style.width = '0%';
}

// Exports
window.setRxVolume = setRxVolume;
window.toggleAudio = toggleAudio;
window.startAudio  = startAudio;
window.stopAudio   = stopAudio;
window.toggleMic   = toggleMic;
window.startMic    = startMic;
window.stopMic     = stopMic;
