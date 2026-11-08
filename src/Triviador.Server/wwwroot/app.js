"use strict";

// ------------------------------------------------------------------ identity

const HUB_URL = (window.TRIVIADOR_CONFIG && window.TRIVIADOR_CONFIG.hubUrl) || "/hubs/game";

const store = {
  get(key) { try { return localStorage.getItem(key); } catch { return null; } },
  set(key, value) { try { localStorage.setItem(key, value); } catch { /* private mode */ } },
};

// Dev identity: a random id kept per browser. Open a private window to be a different player.
const playerId = (() => {
  let id = store.get("triviador.playerId");
  if (!id || !/^[A-Za-z0-9-]{8,64}$/.test(id)) {
    id = (crypto.randomUUID ? crypto.randomUUID() : String(Math.random()).slice(2) + Date.now());
    store.set("triviador.playerId", id);
  }
  return id;
})();

// ------------------------------------------------------------------ state

let conn = null;
let connectedName = null;
let map = null;            // MapDto
let state = null;          // GameStateDto
let clockOffset = 0;       // serverNow - clientNow, in ms
let answerers = [];        // who the open question is for
let lastAttack = null;     // { attacker, defender, territoryId }
let lastResult = null;     // { question, correctAnswer, results }
let pickedOption = null;   // what I clicked on the open MC question
let renderedQuestionKey = null;

const $ = (sel) => document.querySelector(sel);
const esc = (s) => String(s).replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

// ------------------------------------------------------------------ connection

async function connect(name) {
  if (conn && connectedName === name && conn.state === signalR.HubConnectionState.Connected) return;
  if (conn) { try { await conn.stop(); } catch { /* ignore */ } }

  connectedName = name;
  const url = `${HUB_URL}?playerId=${encodeURIComponent(playerId)}&name=${encodeURIComponent(name)}`;
  conn = new signalR.HubConnectionBuilder()
    .withUrl(url)
    .withAutomaticReconnect()
    .build();

  conn.on("MatchJoined", onMatchJoined);
  conn.on("MatchUpdated", onMatchUpdated);
  conn.on("QueueStatus", renderQueue);

  conn.onreconnecting(() => setConnStatus("Reconnecting…"));
  conn.onreconnected(() => setConnStatus("Connected"));
  conn.onclose(() => setConnStatus("Disconnected — refresh to reconnect"));

  setConnStatus("Connecting…");
  await conn.start();
  setConnStatus("Connected");
}

function setConnStatus(text) { $("#conn-status").textContent = text; }

async function invoke(method, ...args) {
  try {
    await conn.invoke(method, ...args);
  } catch (err) {
    showError(err);
  }
}

function showError(err) {
  const msg = String((err && err.message) || err).replace(/^.*HubException: /, "");
  const toast = $("#toast");
  toast.textContent = msg;
  toast.hidden = false;
  clearTimeout(showError.timer);
  showError.timer = setTimeout(() => { toast.hidden = true; }, 3500);
}

// ------------------------------------------------------------------ server messages

function onMatchJoined(mapDto, s) {
  map = mapDto;
  state = null;
  answerers = s.pendingAnswerers || [];
  lastAttack = null;
  lastResult = null;
  pickedOption = null;
  renderedQuestionKey = null;
  $("#log").innerHTML = "";
  $("#queue").hidden = true;
  $("#finished").hidden = true;
  $("#lobby").hidden = true;
  $("#game").hidden = false;
  buildMap();
  applyState(s);
}

function onMatchUpdated(events, s) {
  if (!state || s.matchId !== state.matchId) return; // an old match we already left
  for (const e of events) handleEvent(e.type, e.data);
  applyState(s);
}

function applyState(s) {
  state = s;
  clockOffset = Date.parse(s.serverNow) - Date.now();
  if (s.phase === "BattleAttack") lastAttack = null;
  render();
}

// ------------------------------------------------------------------ events → log

function handleEvent(type, d) {
  switch (type) {
    case "MatchStarted":
      log("The match begins. Place your castles!");
      break;
    case "CastlePlaced":
      log(`${who(d.player)} built a castle in <b>${terr(d.territoryId)}</b>.`);
      break;
    case "QuestionAsked":
      answerers = d.answerers;
      pickedOption = null;
      break;
    case "QuestionResolved":
      lastResult = { question: state && state.question, correctAnswer: d.correctAnswer, results: d.results };
      break;
    case "PicksAwarded": {
      const counts = new Map();
      for (const p of d.pickOrder) counts.set(p, (counts.get(p) || 0) + 1);
      log([...counts].map(([p, n]) => `${who(p)} claims ${n}`).join(", ") + ".");
      break;
    }
    case "TerritoryClaimed":
      log(`${who(d.player)} took <b>${terr(d.territoryId)}</b>.`);
      break;
    case "BattleRoundStarted":
      log(`Battle round ${d.round}`, "round-line");
      break;
    case "AttackDeclared":
      lastAttack = d;
      log(`${who(d.attacker)} attacks ${who(d.defender)} at <b>${terr(d.territoryId)}</b>!`);
      break;
    case "TiebreakStarted":
      log("Both answered the same way — tiebreak!");
      break;
    case "TerritoryConquered":
      log(`${who(d.attacker)} conquered <b>${terr(d.territoryId)}</b>.`);
      break;
    case "AttackRepelled":
      log(`${who(d.defender)} held <b>${terr(d.territoryId)}</b>.`);
      break;
    case "CastleDamaged":
      log(`${who(d.attacker)} hit ${who(d.owner)}'s castle — ${d.hitPointsLeft} left.`);
      break;
    case "PlayerEliminated":
      log(`${who(d.player)}'s castle fell! ${who(d.by)} takes ${d.territoriesTransferred} territories.`);
      break;
    case "MatchFinished":
      log("The match is over.", "round-line");
      break;
  }
}

function log(html, cls) {
  const li = document.createElement("li");
  li.innerHTML = html;
  if (cls) li.className = cls;
  const list = $("#log");
  list.appendChild(li);
  list.scrollTop = list.scrollHeight;
}

// ------------------------------------------------------------------ lookups

function player(id) { return state && state.players.find((p) => p.id === id); }
function seatOf(id) { const p = player(id); return p ? p.seat : -1; }
function nameOf(id) { const p = player(id); return p ? p.name : "?"; }
function who(id) { return `<span class="n${seatOf(id)}">${esc(id === playerId ? "You" : nameOf(id))}</span>`; }
function terr(id) { const t = map && map.territories.find((x) => x.id === id); return esc(t ? t.name : `#${id}`); }
function isMe(id) { return id === playerId; }

// ------------------------------------------------------------------ map

const R = 52;                       // hex radius (centre to corner)
const W = Math.sqrt(3) * R;         // hex width
const MARGIN = 14;

function hexCenter(t) {
  return {
    x: MARGIN + W / 2 + W * (t.column + (t.row % 2 ? 0.5 : 0)),
    y: MARGIN + R + 1.5 * R * t.row,
  };
}

function hexPoints(cx, cy, r) {
  const pts = [];
  for (let i = 0; i < 6; i++) {
    const a = (Math.PI / 180) * (60 * i - 30);
    pts.push(`${(cx + r * Math.cos(a)).toFixed(1)},${(cy + r * Math.sin(a)).toFixed(1)}`);
  }
  return pts.join(" ");
}

function buildMap() {
  const svg = $("#map");
  const cols = Math.max(...map.territories.map((t) => t.column)) + 1;
  const rows = Math.max(...map.territories.map((t) => t.row)) + 1;
  const width = MARGIN * 2 + W * (cols + 0.5);
  const height = MARGIN * 2 + R * 2 + 1.5 * R * (rows - 1);
  svg.setAttribute("viewBox", `0 0 ${width.toFixed(0)} ${height.toFixed(0)}`);

  let html = `
    <defs>
      <radialGradient id="hexShade" cx="50%" cy="35%" r="70%">
        <stop offset="0%" stop-color="#fff" stop-opacity=".16"/>
        <stop offset="100%" stop-color="#000" stop-opacity=".22"/>
      </radialGradient>
    </defs>`;

  for (const t of map.territories) {
    const { x, y } = hexCenter(t);
    html += `
      <g class="hex owner-none" data-id="${t.id}">
        <polygon points="${hexPoints(x, y, R - 2)}"/>
        <polygon class="shade" points="${hexPoints(x, y, R - 2)}"/>
        <polygon class="ring" points="${hexPoints(x, y, R - 7)}" fill="none" stroke="none"/>
        <text class="t-name" x="${x}" y="${y + 22}">${esc(t.name)}</text>
        <text class="t-value" x="${x}" y="${y + 35}">${t.value}</text>
        <g class="castle-slot" transform="translate(${x} ${y - 8})"></g>
      </g>`;
  }
  svg.innerHTML = html;

  svg.onclick = (ev) => {
    const g = ev.target.closest(".hex.valid");
    if (!g) return;
    const id = Number(g.dataset.id);
    const method = { CastlePlacement: "PlaceCastle", ExpansionPicks: "ClaimTerritory", BattleAttack: "Attack" }[state.phase];
    if (method) invoke(method, id);
  };
}

function castleSvg(hp, maxHp) {
  let pips = "";
  for (let i = 0; i < maxHp; i++) {
    pips += `<circle class="hp-pip ${i < hp ? "on" : "off"}" cx="${(i - (maxHp - 1) / 2) * 10}" cy="12" r="3.6"/>`;
  }
  return `
    <g class="castle">
      <path d="M-13 6 V-9 h5 v4 h4 v-4 h8 v4 h4 v-4 h5 V6 Z M-3 6 v-6 a3 3 0 0 1 6 0 v6 Z"/>
      ${pips}
    </g>`;
}

function renderMap() {
  const myTurn = isMe(state.currentTurn);
  const valid = new Set(myTurn ? state.validTargets : []);
  const castles = new Map(state.players.filter((p) => p.castleTerritoryId != null && !p.eliminated)
    .map((p) => [p.castleTerritoryId, p]));

  for (const g of document.querySelectorAll("#map .hex")) {
    const id = Number(g.dataset.id);
    const owner = state.owners[id];
    const seat = owner ? seatOf(owner) : -1;

    g.classList.remove("owner-none", "owner-0", "owner-1", "owner-2");
    g.classList.add(seat >= 0 ? `owner-${seat}` : "owner-none");
    g.classList.toggle("valid", valid.has(id));
    g.classList.toggle("contested", !!lastAttack && lastAttack.territoryId === id);

    const castle = castles.get(id);
    const slot = g.querySelector(".castle-slot");
    const key = castle ? `${castle.id}:${castle.castleHitPoints}` : "";
    if (slot.dataset.key !== key) {
      slot.dataset.key = key;
      slot.innerHTML = castle ? castleSvg(castle.castleHitPoints, 3) : "";
    }
  }
}

// ------------------------------------------------------------------ panels

function render() {
  renderBanner();
  renderPlayers();
  renderMap();
  renderQuestion();
  renderResult();
  renderFinished();
}

function renderBanner() {
  const turn = state.currentTurn;
  const mine = isMe(turn);
  const you = (s) => `<span class="you">${s}</span>`;
  const att = lastAttack;

  const text = {
    CastlePlacement: mine ? you("Place your castle") : `${who(turn)} is placing a castle`,
    ExpansionQuestion: "Everyone answers — the closest guess claims 2 territories",
    ExpansionPicks: mine ? you("Claim a territory next to yours") : `${who(turn)} is claiming`,
    BattleAttack: mine ? you("Choose a territory to attack") : `${who(turn)} is choosing a target`,
    BattleDuel: att ? `${who(att.attacker)} vs ${who(att.defender)} — duel!` : "Duel!",
    BattleTiebreak: att ? `Tiebreak: ${who(att.attacker)} vs ${who(att.defender)}` : "Tiebreak!",
    Finished: "Match over",
  }[state.phase] || "";

  $("#banner").innerHTML = text;
  $("#round").textContent = state.battleRound > 0
    ? `Battle round ${Math.min(state.battleRound, state.battleRounds)} / ${state.battleRounds}`
    : "Expansion";
}

function renderPlayers() {
  $("#players").innerHTML = state.players.map((p) => {
    const hearts = [0, 1, 2].map((i) => `<i class="${i < p.castleHitPoints ? "on" : ""}"></i>`).join("");
    const classes = ["player", `seat-${p.seat}`, p.id === state.currentTurn ? "turn" : "", p.eliminated ? "out" : ""].join(" ");
    return `
      <div class="${classes}">
        <div class="swatch"></div>
        <div>
          <div class="p-name">${esc(p.name)}${isMe(p.id) ? '<span class="tag">YOU</span>' : ""}</div>
          <div class="p-meta">
            <span>${p.territories} land</span>
            <span class="hearts" title="Castle hit points">${hearts}</span>
          </div>
        </div>
        <div class="p-score">${p.score}</div>
      </div>`;
  }).join("");
}

function renderQuestion() {
  const panel = $("#question");
  const q = state.question;
  if (!q) {
    panel.hidden = true;
    renderedQuestionKey = null;
    return;
  }

  const meAnswering = state.pendingAnswerers.includes(playerId);
  const meIn = answerers.includes(playerId) || meAnswering;
  const key = `${q.id}|${state.questionDeadline}|${meAnswering}`;
  if (key === renderedQuestionKey) return; // keep what the player is typing
  renderedQuestionKey = key;

  const forWhom = answerers.length === state.players.filter((p) => !p.eliminated).length
    ? "Everyone"
    : answerers.map((a) => (isMe(a) ? "You" : esc(nameOf(a)))).join(" vs ");

  let body;
  if (meAnswering && q.kind === "MultipleChoice") {
    body = `<div class="options">${q.options.map((o, i) =>
      `<button class="btn option" data-i="${i}">${esc(o)}</button>`).join("")}</div>`;
  } else if (meAnswering) {
    body = `
      <form class="numeric" autocomplete="off">
        <input type="number" step="any" inputmode="decimal" placeholder="Your guess" required>
        <button class="btn primary">Lock in</button>
      </form>
      ${q.unit ? `<div class="unit">Answer in ${esc(q.unit)}</div>` : ""}`;
  } else if (meIn) {
    const picked = pickedOption != null && q.options ? ` — ${esc(q.options[pickedOption])}` : "";
    body = `<div class="locked">Answer locked in${picked}. Waiting for the others…</div>`;
  } else {
    const opts = q.options ? `<div class="options">${q.options.map((o) =>
      `<button class="btn option" disabled>${esc(o)}</button>`).join("")}</div>` : "";
    body = `${opts}<div class="locked">You're watching this one.</div>`;
  }

  panel.innerHTML = `
    <div class="q-head"><span class="q-cat">${esc(q.category)}</span><span class="q-who">${forWhom}</span></div>
    <div class="q-text">${esc(q.text)}</div>
    <div class="timer"><div></div></div>
    ${body}`;
  panel.hidden = false;

  for (const b of panel.querySelectorAll(".option[data-i]")) {
    b.onclick = () => {
      pickedOption = Number(b.dataset.i);
      for (const o of panel.querySelectorAll(".option")) o.disabled = true;
      b.classList.add("picked");
      invoke("AnswerChoice", pickedOption);
    };
  }

  const form = panel.querySelector("form.numeric");
  if (form) {
    const input = form.querySelector("input");
    input.focus({ preventScroll: true });
    form.onsubmit = (ev) => {
      ev.preventDefault();
      const value = Number(input.value);
      if (!Number.isFinite(value) || input.value.trim() === "") return;
      form.querySelector("button").disabled = true;
      input.disabled = true;
      invoke("AnswerNumber", value);
    };
  }
}

function renderResult() {
  const panel = $("#result");
  if (!lastResult || state.question) { panel.hidden = true; return; }

  const q = lastResult.question;
  const rows = lastResult.results.map((r) => {
    let mark = '<span class="bad">✕</span>', detail = "no answer";
    if (r.given && r.given.kind === "choice") {
      mark = r.correct ? '<span class="ok">✓</span>' : '<span class="bad">✕</span>';
      detail = esc(q && q.options ? q.options[r.given.optionIndex] : `option ${r.given.optionIndex + 1}`);
    } else if (r.given && r.given.kind === "numeric") {
      mark = r.correct ? '<span class="ok">✓</span>' : '<span class="t">≈</span>';
      detail = `${fmt(r.given.value)} <span class="t">(off by ${fmt(r.error)})</span>`;
    }
    const time = r.elapsed ? `<span class="t">${seconds(r.elapsed)}s</span>` : "";
    return `<li>${mark}<span>${who(r.player)}: ${detail}</span>${time}</li>`;
  }).join("");

  panel.innerHTML = `
    <div class="panel-title">Last question</div>
    ${q ? `<div class="q-text" style="font-size:14px;margin-top:0">${esc(q.text)}</div>` : ""}
    <div class="r-answer">Answer: <b>${esc(lastResult.correctAnswer)}</b></div>
    <ul class="r-list">${rows}</ul>`;
  panel.hidden = false;
}

function renderFinished() {
  const overlay = $("#finished");
  if (state.phase !== "Finished" || !state.standings) { overlay.hidden = true; return; }

  const mine = state.standings.find((s) => isMe(s.player));
  $("#finish-title").textContent = mine && mine.place === 1 ? "Victory!" : "Match over";
  $("#standings").innerHTML = state.standings.map((s) => `
    <li>
      <span class="place">${s.place}</span>
      <span>${who(s.player)}${s.eliminated ? ' <span class="t">(castle fell)</span>' : ""}</span>
      <b>${s.score}</b>
    </li>`).join("");
  overlay.hidden = false;
}

// ------------------------------------------------------------------ timer

function tickTimer() {
  const bar = document.querySelector("#question .timer");
  if (bar && state && state.question && state.questionDeadline) {
    const total = state.question.kind === "Numeric" ? 20000 : 15000;
    const left = Date.parse(state.questionDeadline) - (Date.now() + clockOffset);
    const frac = Math.max(0, Math.min(1, left / total));
    bar.firstElementChild.style.transform = `scaleX(${frac})`;
    bar.classList.toggle("low", frac < 0.25);
  }
  requestAnimationFrame(tickTimer);
}

// ------------------------------------------------------------------ formatting

function fmt(n) {
  if (n == null) return "";
  return Math.abs(n) >= 1000 ? Math.round(n).toLocaleString() : String(Math.round(n * 100) / 100);
}

/** TimeSpan arrives as "hh:mm:ss.fffffff". */
function seconds(span) {
  const m = /^(?:(\d+)\.)?(\d+):(\d+):(\d+(?:\.\d+)?)$/.exec(span);
  if (!m) return "?";
  const total = (Number(m[1] || 0) * 86400) + Number(m[2]) * 3600 + Number(m[3]) * 60 + Number(m[4]);
  return total.toFixed(1);
}

// ------------------------------------------------------------------ queue & lobby

function renderQueue(q) {
  const box = $("#queue");
  if (!q.searching) { box.hidden = true; setLobbyBusy(false); return; }
  box.hidden = false;
  setLobbyBusy(true);
  $("#queue-text").textContent = `Searching… ${q.playersWaiting}/3 players`;
  $("#queue-sub").textContent = q.secondsUntilBots > 0 ? `Bots fill empty seats in ${q.secondsUntilBots}s` : "Adding bots…";
}

function setLobbyBusy(busy) {
  $("#btn-bots").disabled = busy;
  $("#btn-find").disabled = busy;
}

function currentName() {
  const name = $("#name-input").value.trim().slice(0, 20) || "Player";
  store.set("triviador.name", name);
  return name;
}

async function start() {
  const nameInput = $("#name-input");
  nameInput.value = store.get("triviador.name") || `Player-${playerId.slice(0, 4)}`;

  $("#btn-bots").onclick = async () => {
    try { await connect(currentName()); } catch (e) { return showError(e); }
    invoke("PlayVsBots");
  };
  $("#btn-find").onclick = async () => {
    try { await connect(currentName()); } catch (e) { return showError(e); }
    invoke("FindMatch");
  };
  $("#btn-cancel").onclick = () => invoke("CancelSearch");
  $("#btn-lobby").onclick = () => {
    invoke("LeaveMatch");
    state = null;
    $("#finished").hidden = true;
    $("#game").hidden = true;
    $("#lobby").hidden = false;
  };

  requestAnimationFrame(tickTimer);

  // Connect right away so a page refresh drops you back into a running match.
  try { await connect(currentName()); } catch (e) { setConnStatus("Can't reach the server"); }
}

start();
