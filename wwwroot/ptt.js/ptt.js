// === HamDeck — PTT state machine ===
// Loaded before app.js. Exports to window: pttState, startPTT, stopPTT, togglePTT.

const PTT_TIMEOUT_SECONDS = 180; // 3-minute hard timeout

let pttState    = false;
let pttTimer    = null;
let pttStartTime = null;
let pttCountdownInterval = null;

function togglePTT() {
    if (pttState) stopPTT(); else startPTT();
}

function startPTT() {
    pttState     = true;
    pttStartTime = Date.now();
    window.api('/api/ptt/on');

    const timerEl     = document.getElementById('ptt-timer');
    const countdownEl = document.getElementById('ptt-countdown');
    if (timerEl) timerEl.style.display = 'block';

    pttCountdownInterval = setInterval(() => {
        if (!pttState) return;
        const elapsed   = Math.floor((Date.now() - pttStartTime) / 1000);
        const remaining = PTT_TIMEOUT_SECONDS - elapsed;
        if (remaining <= 0) { stopPTT(); return; }
        const min = Math.floor(remaining / 60);
        const sec = remaining % 60;
        if (countdownEl) countdownEl.textContent = min + ':' + String(sec).padStart(2, '0');
    }, 1000);

    pttTimer = setTimeout(() => {
        console.warn('[HamDeck] PTT timeout reached (3 min)');
        stopPTT();
    }, PTT_TIMEOUT_SECONDS * 1000);
}

function stopPTT() {
    pttState = false;
    window.api('/api/ptt/off');

    if (pttTimer) { clearTimeout(pttTimer); pttTimer = null; }
    if (pttCountdownInterval) { clearInterval(pttCountdownInterval); pttCountdownInterval = null; }
    pttStartTime = null;

    const timerEl = document.getElementById('ptt-timer');
    if (timerEl) timerEl.style.display = 'none';
}

// Exports
window.pttState  = false; // kept in sync manually below
window.startPTT  = startPTT;
window.stopPTT   = stopPTT;
window.togglePTT = togglePTT;

// Keep window.pttState in sync so pollStatus() in app.js can read it
const _origStart = startPTT;
const _origStop  = stopPTT;
startPTT = function() { _origStart(); window.pttState = true; };
stopPTT  = function() { _origStop();  window.pttState = false; };
window.startPTT  = startPTT;
window.stopPTT   = stopPTT;
