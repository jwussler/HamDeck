// === HamDeck Web Dashboard — Direct Polling ===
// Polls the HamDeck API directly from the browser. No middleware needed.

const API = window.location.origin;
const POLL_FAST = 500;    // status + meters: every 500ms
const POLL_SESSION = 2000; // session stats: every 2s
const POLL_CLUSTER = 30000; // dx cluster: every 30s

const el = (id) => document.getElementById(id);

const dom = {
    connBadge:   el("connection-badge"),
    clock:       el("clock"),
    freq:        el("freq-display"),
    band:        el("band-badge"),
    mode:        el("mode-badge"),
    vfo:         el("vfo-badge"),
    split:       el("split-badge"),
    tx:          el("tx-badge"),
    power:       el("power-val"),
    ant:         el("ant-val"),
    rxant:       el("rxant-val"),
    atu:         el("atu-val"),
    smeterBar:   el("smeter-bar"),
    smeterVal:   el("smeter-val"),
    powerBar:    el("power-bar"),
    powerMeter:  el("power-meter-val"),
    swrBar:      el("swr-bar"),
    swrVal:      el("swr-val"),
    alcBar:      el("alc-bar"),
    alcVal:      el("alc-val"),
    recStatus:   el("rec-status"),
    txCount:     el("tx-count"),
    txTime:      el("tx-time"),
    qsyCount:    el("qsy-count"),
    clusterBody: el("cluster-body"),
};

// === HELPERS ===

async function fetchJson(path) {
    try {
        const resp = await fetch(API + path);
        if (resp.ok) return await resp.json();
    } catch {}
    return null;
}

function formatFreq(hz) {
    if (!hz || hz <= 0) return "\u2014";
    const mhz = Math.floor(hz / 1000000);
    const khz = Math.floor((hz % 1000000) / 1000);
    const sub = Math.floor((hz % 1000) / 10);
    return `${mhz}.${String(khz).padStart(3, "0")}.${String(sub).padStart(2, "0")}`;
}

function freqToBand(hz) {
    if (!hz) return "\u2014";
    const mhz = hz / 1000000;
    if (mhz >= 1.8 && mhz <= 2.0) return "160m";
    if (mhz >= 3.5 && mhz <= 4.0) return "80m";
    if (mhz >= 5.3 && mhz <= 5.4) return "60m";
    if (mhz >= 7.0 && mhz <= 7.3) return "40m";
    if (mhz >= 10.1 && mhz <= 10.15) return "30m";
    if (mhz >= 14.0 && mhz <= 14.35) return "20m";
    if (mhz >= 18.068 && mhz <= 18.168) return "17m";
    if (mhz >= 21.0 && mhz <= 21.45) return "15m";
    if (mhz >= 24.89 && mhz <= 24.99) return "12m";
    if (mhz >= 28.0 && mhz <= 29.7) return "10m";
    if (mhz >= 50.0 && mhz <= 54.0) return "6m";
    return "\u2014";
}

function sMeterText(raw) {
    if (raw <= 0) return "S0";
    if (raw < 28) return "S1";
    if (raw < 42) return "S2";
    if (raw < 56) return "S3";
    if (raw < 70) return "S4";
    if (raw < 84) return "S5";
    if (raw < 98) return "S6";
    if (raw < 112) return "S7";
    if (raw < 128) return "S8";
    if (raw < 148) return "S9";
    if (raw < 168) return "S9+10";
    if (raw < 188) return "S9+20";
    if (raw < 218) return "S9+30";
    if (raw < 238) return "S9+40";
    return "S9+50";
}

// === POLLERS ===

async function pollStatus() {
    const data = await fetchJson("/api/status");
    if (!data) {
        dom.connBadge.textContent = "OFFLINE";
        dom.connBadge.className = "badge badge-offline";
        dom.freq.textContent = "\u2014";
        dom.freq.classList.remove("tx-active");
        dom.band.textContent = "\u2014";
        dom.mode.textContent = "\u2014";
        return;
    }

    if (!data.connected) {
        dom.connBadge.textContent = "RADIO OFF";
        dom.connBadge.className = "badge badge-offline";
        dom.freq.textContent = "\u2014";
        dom.freq.classList.remove("tx-active");
        dom.band.textContent = "\u2014";
        dom.mode.textContent = "\u2014";
        return;
    }

    dom.connBadge.textContent = "CONNECTED";
    dom.connBadge.className = "badge badge-online";

    dom.freq.textContent = formatFreq(data.freq);
    dom.band.textContent = freqToBand(data.freq);
    dom.mode.textContent = data.mode || "\u2014";
    dom.vfo.textContent = `VFO ${data.vfo || "A"}`;

    if (data.tx) {
        dom.tx.classList.remove("badge-hidden");
        dom.freq.classList.add("tx-active");
    } else {
        dom.tx.classList.add("badge-hidden");
        dom.freq.classList.remove("tx-active");
    }

    if (data.split) dom.split.classList.remove("badge-hidden");
    else dom.split.classList.add("badge-hidden");

    dom.power.textContent = data.power ? `${data.power}W` : "\u2014";
    dom.ant.textContent = data.ant ? `ANT ${data.ant}` : "\u2014";
    dom.rxant.textContent = data.rxant ? "ON" : "OFF";

    const tuning = data.amp_tuning || data.tgxl_tuning;
    dom.atu.textContent = tuning ? "TUNING" : "READY";
    dom.atu.style.color = tuning ? "var(--accent-yellow)" : "";
}

async function pollMeters() {
    const data = await fetchJson("/api/meters");
    if (!data || data.status !== "ok") return;

    const sRaw = data.s_meter || 0;
    dom.smeterBar.style.width = Math.min(100, (sRaw / 255) * 100) + "%";
    dom.smeterVal.textContent = sMeterText(sRaw);

    const pRaw = data.power || 0;
    dom.powerBar.style.width = Math.min(100, (pRaw / 255) * 100) + "%";
    dom.powerMeter.textContent = Math.round((pRaw / 255) * 200) + "W";

    const swrRaw = data.swr || 0;
    const swrVal = 1.0 + (swrRaw / 255) * 4.0;
    dom.swrBar.style.width = Math.min(100, (swrRaw / 255) * 100) + "%";
    dom.swrVal.textContent = swrVal.toFixed(1);
    dom.swrBar.className = "meter-bar meter-bar-swr";
    if (swrVal > 3.0) dom.swrBar.classList.add("swr-danger");
    else if (swrVal > 2.0) dom.swrBar.classList.add("swr-warn");

    const alcRaw = data.alc || 0;
    dom.alcBar.style.width = Math.min(100, (alcRaw / 255) * 100) + "%";
    dom.alcVal.textContent = alcRaw;
}

async function pollRecording() {
    const data = await fetchJson("/api/record/status");
    if (!data) return;

    if (data.recording) {
        dom.recStatus.textContent = "REC";
        dom.recStatus.className = "rec-indicator rec-on";
    } else if (data.buffering) {
        dom.recStatus.textContent = "BUFFER";
        dom.recStatus.className = "rec-indicator rec-off";
    } else {
        dom.recStatus.textContent = "OFF";
        dom.recStatus.className = "rec-indicator rec-off";
    }
}

async function pollSession() {
    const data = await fetchJson("/api/session");
    if (!data || data.status !== "ok") return;

    dom.txCount.textContent = data.tx_count ?? "\u2014";
    dom.txTime.textContent = data.tx_time ?? "\u2014";
    dom.qsyCount.textContent = data.qsy_count ?? "\u2014";
}

async function pollCluster() {
    const data = await fetchJson("/api/cluster/spots");
    if (!data || data.status !== "ok") return;

    const spots = data.spots || [];
    if (spots.length === 0) return;

    dom.clusterBody.innerHTML = "";

    spots.forEach((spot) => {
        const tr = document.createElement("tr");
        const band = spot.band || freqToBand(spot.freq_hz || 0);
        tr.className = `spot-${band}`;

        const freqMhz = (spot.freq_khz || 0).toFixed(1);
        const time = spot.time || "";
        const timeStr = time ? new Date(time).toUTCString().slice(17, 22) + "Z" : "";

        tr.innerHTML = `
            <td>${freqMhz}</td>
            <td style="color: var(--accent-cyan); font-weight: 600;">${spot.dx_call || ""}</td>
            <td>${spot.entity || ""} ${spot.flag || ""}</td>
            <td>${spot.spotter || ""}</td>
            <td style="color: var(--text-secondary); font-size: 0.75rem;">${spot.comment || ""}</td>
            <td style="color: var(--text-muted);">${timeStr}</td>
        `;
        dom.clusterBody.appendChild(tr);
    });
}

// === CLOCK ===

function updateClock() {
    const now = new Date();
    dom.clock.textContent = now.toUTCString().slice(17, 25) + "Z";
}

// === MAIN LOOP ===

async function pollFast() {
    await Promise.all([pollStatus(), pollMeters(), pollRecording()]);
}

// Start polling
setInterval(pollFast, POLL_FAST);
setInterval(pollSession, POLL_SESSION);
setInterval(pollCluster, POLL_CLUSTER);
setInterval(updateClock, 1000);

// Run immediately on load
pollFast();
pollSession();
pollCluster();
updateClock();
