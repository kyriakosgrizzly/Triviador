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
let lobby = null;          // LobbyDto of the private game I'm in, if any
let inviteCode = readInviteCode(); // ?game=CODE from an invite link, until joined or declined

function readInviteCode() {
  const code = new URLSearchParams(location.search).get("game");
  return code && /^[A-Za-z0-9]{4,12}$/.test(code) ? code.toUpperCase() : null;
}

/** Keeps ?game=CODE in the address bar while in a private game, so a refresh rejoins it. */
function setUrlCode(code) {
  const url = code ? `${location.pathname}?game=${encodeURIComponent(code)}` : location.pathname;
  history.replaceState(null, "", url);
}

function shareLink(code) { return `${location.origin}${location.pathname}?game=${encodeURIComponent(code)}`; }

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
  conn.on("LobbyUpdated", onLobbyUpdated);
  conn.on("ConquestJoined", (rules, s) => Conquest.joined(rules, s));
  conn.on("ConquestUpdated", (events, s) => Conquest.updated(events, s));
  conn.on("CardsJoined", (rules, s) => Cards.joined(rules, s));
  conn.on("CardsUpdated", (events, s) => Cards.updated(events, s));

  conn.onreconnecting(() => setConnStatus("Reconnecting…"));
  conn.onreconnected(() => {
    setConnStatus("Connected");
    // The server drops you from a private game when the connection drops; step back in.
    if (lobby && !state) invoke("JoinPrivateGame", lobby.code);
  });
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

/** Shared clean-up when any game starts: leave the lobby/invite state behind. */
function enteringGame() {
  lobby = null;
  inviteCode = null;
  setUrlCode(null);
  store.set("triviador.lobby", "");
  $("#queue").hidden = true;
  setLobbyBusy(false);
}

function onMatchJoined(mapDto, s) {
  map = mapDto;
  state = null;
  answerers = s.pendingAnswerers || [];
  lastAttack = null;
  lastResult = null;
  pickedOption = null;
  renderedQuestionKey = null;
  enteringGame();
  Conquest.reset();
  Cards.reset();
  $("#log").innerHTML = "";
  $("#queue").hidden = true;
  $("#finished").hidden = true;
  $("#confirm-leave").hidden = true;
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
    const classes = ["player", `seat-${p.seat}`, p.id === state.currentTurn ? "turn" : "",
      p.eliminated ? "out" : "", p.left ? "left" : ""].join(" ");
    const tag = isMe(p.id) ? '<span class="tag">YOU</span>' : p.left ? '<span class="tag">LEFT · BOT PLAYS</span>' : "";
    return `
      <div class="${classes}">
        <div class="swatch"></div>
        <div>
          <div class="p-name">${esc(p.name)}${tag}</div>
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
  $("#btn-leave").hidden = state.phase === "Finished";
  if (state.phase !== "Finished" || !state.standings) { overlay.hidden = true; return; }
  $("#confirm-leave").hidden = true;

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
  const what = q.mode === "cards" ? "Quiz Cards" : "Quick match";
  $("#queue-text").textContent = `${what}: searching… ${q.playersWaiting}/${q.seats || 3} players`;
  const addBots = $("#btn-add-bots");
  if (q.secondsUntilBots >= 0) {
    // Server configured to fill with bots automatically.
    $("#queue-sub").textContent = q.secondsUntilBots > 0 ? `Bots fill empty seats in ${q.secondsUntilBots}s` : "Adding bots…";
    addBots.hidden = true;
  } else {
    const missing = q.botsToAdd || 0;
    $("#queue-sub").textContent = missing > 0
      ? "Waiting for more players — or fill the empty seats with bots."
      : "Starting…";
    addBots.hidden = missing === 0;
    addBots.disabled = false;
    addBots.textContent = `Add ${missing} bot${missing === 1 ? "" : "s"} and start`;
  }
}

function setLobbyBusy(busy) {
  $("#btn-bots").disabled = busy;
  $("#btn-find").disabled = busy;
  $("#btn-invite").disabled = busy;
  $("#btn-conquest").disabled = busy;
  $("#btn-cards").disabled = busy;
  $("#btn-cards-find").disabled = busy;
  $("#btn-cards-invite").disabled = busy;
}

let conquestSeats = 3;
let cardsSeats = 3;

// ------------------------------------------------------------------ private games

function onLobbyUpdated(l) {
  lobby = l;
  if (l) {
    inviteCode = null;
    setUrlCode(l.code);
    store.set("triviador.lobby", l.code);
  } else {
    store.set("triviador.lobby", "");
    if (!state) setUrlCode(inviteCode);
  }
  renderPrivate();
}

function renderPrivate() {
  const inLobby = !!lobby;
  $("#private").hidden = !inLobby;
  $("#invite").hidden = inLobby || !inviteCode;
  $("#lobby-actions").hidden = inLobby;
  if (inviteCode) $("#invite-code").textContent = inviteCode;
  if (!inLobby) return;

  const amHost = lobby.host === playerId;
  $("#private-code").textContent = lobby.code;
  const friends = lobby.maxPlayers - 1;
  $("#private-invite-sub").textContent = `Send this link to up to ${friends} friend${friends === 1 ? "" : "s"}`;
  $("#share-link").value = shareLink(lobby.code);
  $("#btn-share").hidden = !navigator.share;

  const rows = lobby.players.map((p) => {
    const tags = [p.id === lobby.host ? "HOST" : "", isMe(p.id) ? "YOU" : ""].filter(Boolean)
      .map((t) => `<span class="tag">${t}</span>`).join("");
    return `<li><span>${esc(p.name)}${tags}</span></li>`;
  });
  for (let i = lobby.players.length; i < lobby.maxPlayers; i++) rows.push('<li class="empty">Waiting for a friend… (or a bot)</li>');
  $("#private-players").innerHTML = rows.join("");

  const conquest = lobby.mode === "conquest";
  const flexible = conquest || lobby.mode === "cards";
  for (const b of document.querySelectorAll("#private-mode .mode-btn")) {
    b.classList.toggle("on", b.dataset.mode === lobby.mode);
    b.disabled = !amHost;
  }
  $("#private-seats").hidden = !flexible;
  for (const b of document.querySelectorAll("#private-seats .seat")) {
    const n = Number(b.dataset.seats);
    b.classList.toggle("on", n === lobby.maxPlayers);
    b.disabled = !amHost || n < lobby.players.length;
  }
  $("#private-mode").hidden = !amHost;
  const modeName = { quick: "Quick match", conquest: "Conquest", cards: "Quiz Cards" }[lobby.mode] || "Quick match";
  $("#private-mode-label").textContent = amHost ? "" : `${modeName} · ${lobby.maxPlayers} players`;

  const empty = lobby.maxPlayers - lobby.players.length;
  $("#btn-start").hidden = !amHost;
  $("#private-note").textContent = amHost
    ? (empty > 0 ? `Start whenever you're ready. ${empty === 1 ? "The empty seat gets a bot" : "Empty seats get bots"}.` : "Everyone's here!")
    : "Waiting for the host to start the game…";
}

async function copyLink() {
  const input = $("#share-link");
  try {
    await navigator.clipboard.writeText(input.value);
  } catch {
    input.select();
    document.execCommand("copy");
  }
  const btn = $("#btn-copy");
  btn.textContent = "Copied!";
  setTimeout(() => { btn.textContent = "Copy"; }, 1500);
}

// ------------------------------------------------------------------ navigation

function goToLobby() {
  state = null;
  Conquest.reset();
  Cards.reset();
  $("#finished").hidden = true;
  $("#confirm-leave").hidden = true;
  $("#game").hidden = true;
  $("#lobby").hidden = false;
  renderPrivate();
}

function currentName() {
  const name = $("#name-input").value.trim().slice(0, 20) || "Player";
  store.set("triviador.name", name);
  return name;
}

/** Connects with the current name, then runs a hub call. Returns its result, or undefined on error. */
async function act(method, ...args) {
  try {
    await connect(currentName());
    return await conn.invoke(method, ...args);
  } catch (e) {
    showError(e);
    return undefined;
  }
}

async function start() {
  const nameInput = $("#name-input");
  nameInput.value = store.get("triviador.name") || `Player-${playerId.slice(0, 4)}`;

  $("#btn-bots").onclick = () => act("PlayVsBots");
  $("#btn-find").onclick = () => act("FindMatch");
  $("#btn-cancel").onclick = () => invoke("CancelSearch");

  $("#btn-invite").onclick = () => act("CreatePrivateGame");
  $("#btn-conquest").onclick = () => act("PlayConquestVsBots", conquestSeats);
  for (const b of document.querySelectorAll("#mode-conquest .seat")) {
    b.onclick = () => {
      conquestSeats = Number(b.dataset.seats);
      for (const x of document.querySelectorAll("#mode-conquest .seat")) x.classList.toggle("on", x === b);
    };
  }
  $("#btn-cards").onclick = () => act("PlayCardsVsBots", cardsSeats);
  $("#btn-cards-find").onclick = () => act("FindCardsMatch");
  $("#btn-cards-invite").onclick = () => act("CreatePrivateGameFor", "cards", cardsSeats);
  $("#btn-add-bots").onclick = () => { $("#btn-add-bots").disabled = true; invoke("StartQueueWithBots"); };
  for (const b of document.querySelectorAll("#mode-cards .seat")) {
    b.onclick = () => {
      cardsSeats = Number(b.dataset.seats);
      for (const x of document.querySelectorAll("#mode-cards .seat")) x.classList.toggle("on", x === b);
    };
  }
  for (const b of document.querySelectorAll("#private-mode .mode-btn")) {
    b.onclick = () => invoke("SetPrivateGameMode", b.dataset.mode, lobby ? Math.max(lobby.players.length, 3) : 3);
  }
  for (const b of document.querySelectorAll("#private-seats .seat")) {
    b.onclick = () => invoke("SetPrivateGameMode", lobby ? lobby.mode : "conquest", Number(b.dataset.seats));
  }
  Conquest.wire();
  Cards.wire();
  Conquest.loadWorld().catch(() => {});
  $("#btn-join-invite").onclick = async () => {
    const code = inviteCode;
    try {
      await connect(currentName());
      await conn.invoke("JoinPrivateGame", code);
    } catch (e) {
      showError(e);
      inviteCode = null;   // dead or full game: drop the link and show the normal lobby
      setUrlCode(null);
      renderPrivate();
    }
  };
  $("#btn-decline").onclick = () => { inviteCode = null; setUrlCode(null); renderPrivate(); };
  $("#btn-copy").onclick = copyLink;
  $("#btn-share").onclick = () => navigator.share({ title: "Triviador", text: "Join my Triviador game!", url: $("#share-link").value }).catch(() => {});
  $("#btn-start").onclick = () => invoke("StartPrivateGame");
  $("#btn-leave-private").onclick = () => invoke("LeavePrivateGame");

  $("#btn-leave").onclick = () => { $("#confirm-leave").hidden = false; };
  $("#btn-stay").onclick = () => { $("#confirm-leave").hidden = true; };
  $("#btn-confirm-leave").onclick = () => { invoke("LeaveMatch"); goToLobby(); };
  $("#btn-lobby").onclick = () => { invoke("LeaveMatch"); goToLobby(); };

  renderPrivate();
  requestAnimationFrame(tickTimer);

  // Connect right away so a page refresh drops you back into a running match.
  try { await connect(currentName()); } catch (e) { setConnStatus("Can't reach the server"); return; }

  // Refreshed while waiting in a private game: step straight back in.
  if (inviteCode && store.get("triviador.lobby") === inviteCode && !state) {
    invoke("JoinPrivateGame", inviteCode);
  }
}

start();
