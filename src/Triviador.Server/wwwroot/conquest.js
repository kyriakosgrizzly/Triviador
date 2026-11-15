"use strict";

// Conquest screen. Uses helpers from app.js at call time: conn, playerId, $, esc, invoke, showError,
// enteringGame, goToLobby.

const Conquest = (() => {
  const COLORS = {
    crimson: "#e5484d", azure: "#3e9bff", amber: "#f5b942",
    emerald: "#3fbf7f", violet: "#a77bf3", copper: "#d9844a",
  };
  const RES_CLASS = { Gold: "gold", Manpower: "manpower", Production: "production" };
  const RES_COLOR = { Gold: "var(--r-gold)", Manpower: "var(--r-manpower)", Production: "var(--r-production)" };
  const BLD_LETTER = { Barracks: "B", Workshop: "W", Market: "M", Fort: "F", University: "U" };
  const NEUTRAL = "#4a5368";

  let world = null;
  let worldPromise = null;
  let rules = null;
  let s = null;                 // ConquestStateDto
  let active = false;
  let clockOffset = 0;

  let selected = null;          // region id shown in the panel
  let orders = [];              // this round's plan, built locally
  let planRound = -1;
  let editing = true;
  let lastResult = null;
  let renderedQuestionKey = null;
  let pickedOption = null;
  let flashUntil = new Map();   // region id -> time

  let view = { x: 0, y: 0, w: 1600, h: 792 };

  // ================================================================== lifecycle

  function loadWorld() {
    worldPromise ??= fetch("maps/world.json").then((r) => r.json()).then((w) => (world = w));
    return worldPromise;
  }

  async function joined(r, state) {
    rules = r;
    try { await loadWorld(); } catch (e) { showError("Couldn't load the world map."); return; }
    enteringGame();
    active = true;
    s = null;
    orders = [];
    planRound = -1;
    selected = null;
    lastResult = null;
    renderedQuestionKey = null;
    $("#cq-log").innerHTML = "";
    $("#lobby").hidden = true;
    $("#game").hidden = true;
    $("#cards").hidden = true;
    $("#conquest").hidden = false;
    $("#cq-finished").hidden = true;
    $("#cq-confirm-leave").hidden = true;
    buildMap();
    setView({ x: 0, y: 0, w: world.width, h: world.height });
    log("The world awaits. Answer to pick your colour first!", "round-line");
    apply(state);
  }

  function updated(events, state) {
    if (!active || !s || state.gameId !== s.gameId) return;
    for (const e of events) handle(e.type, e.data);
    apply(state);
  }

  function reset() {
    active = false;
    s = null;
    $("#conquest").hidden = true;
  }

  function apply(state) {
    s = state;
    clockOffset = Date.parse(state.serverNow) - Date.now();

    if (s.phase === "Planning" && planRound !== s.round) {
      planRound = s.round;
      orders = [];
      editing = true;
      const me = myPlayer();
      if (me && me.capital && region(me.capital).owner === playerId) selected = me.capital;
      else selected = s.regions.find((r) => r.owner === playerId)?.id ?? null;
    }
    render();
  }

  // ================================================================== lookups

  const isMe = (id) => id === playerId;
  const myPlayer = () => s && s.players.find((p) => isMe(p.id));
  const player = (id) => s && s.players.find((p) => p.id === id);
  const region = (id) => s.regions[id - 1];
  const worldRegion = (id) => world.regions[id - 1];
  const regionName = (id) => worldRegion(id).name;
  const colorOf = (id) => (player(id) && player(id).color ? COLORS[player(id).color] : "#9aa3b5");

  function who(id) {
    if (!id) return "neutral";
    const p = player(id);
    const name = isMe(id) ? "You" : p ? p.name : "?";
    return `<b style="color:${colorOf(id)}">${esc(name)}</b>`;
  }

  function walletHtml(w, opts = {}) {
    return ["gold", "manpower", "production"].map((k) =>
      `<span><i class="res res-${k}"></i>${w[k]}</span>`).join(opts.sep ?? " ");
  }

  function costOf(order) {
    if (order.kind === "attack") return rules.weapons.find((w) => w.weapon === order.weapon).cost;
    if (order.kind === "build") return rules.buildings.find((b) => b.building === order.building).cost;
    if (order.kind === "fortify") return rules.fortifyCosts[region(order.region).castle];
    return { gold: 0, manpower: 0, production: 0 };
  }
  const add = (a, b) => ({ gold: a.gold + b.gold, manpower: a.manpower + b.manpower, production: a.production + b.production });
  const sub = (a, b) => ({ gold: a.gold - b.gold, manpower: a.manpower - b.manpower, production: a.production - b.production });
  const mul = (a, k) => ({ gold: a.gold * k, manpower: a.manpower * k, production: a.production * k });
  const covers = (w, c) => w.gold >= c.gold && w.manpower >= c.manpower && w.production >= c.production;
  const plannedCost = () => orders.reduce((acc, o) => add(acc, costOf(o)), { gold: 0, manpower: 0, production: 0 });
  const remaining = () => (s.you ? sub(s.you.wallet, plannedCost()) : { gold: 0, manpower: 0, production: 0 });

  const powerOf = (weapon) => rules.weapons.find((w) => w.weapon === weapon).power;
  const plannedBuildAt = (id) => orders.find((o) => o.kind === "build" && o.region === id);
  const plannedFortify = (id) => orders.some((o) => o.kind === "fortify" && o.region === id);
  const plannedAttacksOn = (id) => orders.filter((o) => o.kind === "attack" && o.to === id);
  const hasUniversity = () => s.regions.some((r) => r.owner === playerId && r.building === "University")
    || orders.some((o) => o.kind === "build" && o.building === "University");

  /** My regions bordering this one (where an attack on it could come from). */
  const myNeighbours = (id) => worldRegion(id).neighbors.filter((n) => region(n).owner === playerId);

  /** The castle level left after the hits already planned on it (assuming they all land). */
  function castleAfterPlanned(id) {
    let level = region(id).castle;
    for (const a of plannedAttacksOn(id)) {
      const p = powerOf(a.weapon);
      if (p > level) return -1; // captured
      level -= p;
    }
    return level;
  }

  // ================================================================== events → log

  function handle(type, d) {
    switch (type) {
      case "ConquestQuestionAsked":
        pickedOption = null;
        break;
      case "ConquestQuestionResolved":
        lastResult = { question: s.question, phase: s.phase, correctAnswer: d.correctAnswer, results: d.results };
        break;
      case "ColorPicked":
        log(`${who(d.player)} takes <b style="color:${COLORS[d.color]}">${esc(d.color)}</b>.`);
        break;
      case "CapitalPicked":
        log(`${who(d.player)} founds a capital in <b>${esc(regionName(d.region))}</b>.`);
        break;
      case "ConquestPhaseChanged":
        if (d.phase === "SupplyQuestion") log(`Round ${d.round}`, "round-line");
        break;
      case "SupplyAwarded":
        log(`${who(d.player)} wins supplies: +${d.bonus.gold} of each.`);
        break;
      case "IncomeCollected":
        if (isMe(d.player)) log(`Income: ${walletHtml(d.income)}`);
        break;
      case "BuildingBuilt":
        log(`${who(d.player)} builds a ${esc(d.building)} in <b>${esc(regionName(d.region))}</b>.`);
        break;
      case "CastleFortified":
        log(`${who(d.player)} raises the castle in <b>${esc(regionName(d.region))}</b> to level ${d.level}.`);
        break;
      case "AttackCancelled":
        if (isMe(d.player)) log(`Your ${esc(d.weapon)} on <b>${esc(regionName(d.to))}</b> was called off (${esc(d.reason)}) and refunded.`);
        break;
      case "BattleFought": {
        const r = d.report;
        const where = `<b>${esc(regionName(r.to))}</b>`;
        const vs = r.defender ? `${who(r.defender)}'s ${where}` : `neutral ${where}`;
        const outcome = r.captured ? "<b>captured!</b>"
          : r.hitLanded ? `castle ${r.castleBefore} → ${r.castleAfter}`
          : "missed";
        log(`${who(r.attacker)}'s ${esc(r.weapon)} hits ${vs} (castle ${r.castleBefore}) — ${esc(r.reason)}: ${outcome}`);
        flashUntil.set(r.to, Date.now() + 3000);
        break;
      }
      case "CapitalCaptured":
        log(`${who(d.by)} captured ${who(d.from)}'s capital!`, "round-line");
        break;
      case "ConquestPlayerEliminated":
        log(`${who(d.player)} has been wiped off the map by ${who(d.by)}.`, "round-line");
        break;
      case "ConquestFinished":
        log("The war is over.", "round-line");
        break;
    }
  }

  function log(html, cls) {
    const li = document.createElement("li");
    li.innerHTML = html;
    if (cls) li.className = cls;
    const list = $("#cq-log");
    list.appendChild(li);
    list.scrollTop = list.scrollHeight;
  }

  // ================================================================== map

  function buildMap() {
    const svg = $("#cq-map");
    const byId = (id) => worldRegion(id);
    const W = world.width, H = world.height;
    const allLand = world.regions.map((r) => `<path d="${r.path}"/>`).join("");
    let html = `
      <defs>
        <marker id="arrow" viewBox="0 0 10 10" refX="8" refY="5" markerWidth="5" markerHeight="5" orient="auto-start-reverse">
          <path d="M0,0 L10,5 L0,10 z" fill="#fff"/>
        </marker>
        <!-- Ocean: deep gradient with faint wave lines -->
        <radialGradient id="ocean" cx="50%" cy="42%" r="75%">
          <stop offset="0" stop-color="#18264a"/>
          <stop offset="1" stop-color="#090e1b"/>
        </radialGradient>
        <pattern id="waves" width="46" height="18" patternUnits="userSpaceOnUse">
          <path d="M0 9 Q11.5 3 23 9 T46 9" fill="none" stroke="#a9c1ef" stroke-width="1" opacity=".09"/>
        </pattern>
        <!-- Shallow water glow along every coast -->
        <filter id="coast" x="-10%" y="-10%" width="120%" height="120%">
          <feGaussianBlur stdDeviation="7"/>
        </filter>
        <!-- Terrain relief: lit fractal noise, clipped to land and multiplied over the owner colours -->
        <filter id="terrain" x="0" y="0" width="100%" height="100%">
          <feTurbulence type="fractalNoise" baseFrequency="0.011" numOctaves="5" seed="11" result="noise"/>
          <feDiffuseLighting in="noise" lighting-color="#ffffff" surfaceScale="7" result="lit">
            <feDistantLight azimuth="225" elevation="52"/>
          </feDiffuseLighting>
          <feComposite in="lit" in2="SourceGraphic" operator="in"/>
        </filter>
        <!-- Paper grain over the whole map -->
        <filter id="grain" x="0" y="0" width="100%" height="100%">
          <feTurbulence type="fractalNoise" baseFrequency="0.9" numOctaves="2" stitchTiles="stitch"/>
          <feColorMatrix values="0 0 0 0 1  0 0 0 0 1  0 0 0 0 1  0 0 0 0.07 0"/>
        </filter>
        <clipPath id="land">${allLand}</clipPath>
      </defs>
      <g class="sea" pointer-events="none">
        <rect width="${W}" height="${H}" fill="url(#ocean)"/>
        <rect width="${W}" height="${H}" fill="url(#waves)"/>
        <g class="coast" filter="url(#coast)" opacity=".5">${world.regions.map((r) =>
          `<path d="${r.path}" fill="none" stroke="#3f6fb3" stroke-width="12"/>`).join("")}</g>
      </g>
      <g class="links">`;
    for (const l of world.seaLinks) {
      const a = byId(l.a).label, b = byId(l.b).label;
      if (l.wrap) {
        html += `<line class="sea-link" x1="${a[0]}" y1="${a[1]}" x2="0" y2="${a[1]}"/>`;
        html += `<line class="sea-link" x1="${b[0]}" y1="${b[1]}" x2="${world.width}" y2="${b[1]}"/>`;
      } else {
        html += `<line class="sea-link" x1="${a[0]}" y1="${a[1]}" x2="${b[0]}" y2="${b[1]}"/>`;
      }
    }
    html += `</g><g class="regions">`;
    for (const r of world.regions) html += `<path class="region neutral" data-id="${r.id}" d="${r.path}"><title>${esc(r.name)}</title></path>`;
    html += `</g>
      <g class="relief" clip-path="url(#land)" pointer-events="none">
        <rect width="${W}" height="${H}" fill="#fff" filter="url(#terrain)"/>
      </g>
      <rect class="grain" width="${W}" height="${H}" filter="url(#grain)" pointer-events="none"/>
      <g class="orders"></g><g class="badges">`;
    for (const r of world.regions) {
      const [x, y] = r.label;
      html += `
        <g class="badge" data-id="${r.id}" transform="translate(${x} ${y})">
          <text class="name" y="-25">${esc(r.name)}</text>
          <circle class="res-dot" cx="-21" cy="-12" r="6"/>
          <path class="castle-shape" d="M-14,12 V-5 h5 v-5 h5 v5 h8 v-5 h5 v5 h5 V12 Z"/>
          <text class="count" y="3">0</text>
          <path class="star" visibility="hidden" transform="translate(17 -14) scale(0.9)"
            d="M0,-9 L2.6,-2.8 9,-2.8 3.9,1.1 5.8,7.5 0,3.6 -5.8,7.5 -3.9,1.1 -9,-2.8 -2.6,-2.8 Z"/>
          <g class="bld" visibility="hidden">
            <rect x="9" y="5" width="16" height="16" rx="4" fill="#e8ebf2"/>
            <text class="bld" x="17" y="13.5">B</text>
          </g>
        </g>`;
    }
    html += `</g>`;
    svg.innerHTML = html;
    attachMapInput(svg);
  }

  function renderMap() {
    const svg = $("#cq-map");
    if (!svg.firstElementChild || !s.regions.length) return;

    const planning = s.phase === "Planning" && editing && s.you && myPlayer() && !myPlayer().eliminated;
    const targets = new Set();
    if (planning && selected && region(selected).owner === playerId) {
      for (const n of worldRegion(selected).neighbors) if (region(n).owner !== playerId) targets.add(n);
    }
    const capitalOk = new Set(s.phase === "CapitalPick" && isMe(s.currentPicker) ? s.validCapitals : []);
    const now = Date.now();
    const showNames = view.w < 1100;

    for (const path of svg.querySelectorAll(".region")) {
      const id = Number(path.dataset.id);
      const r = region(id);
      path.style.fill = r.owner ? colorOf(r.owner) : NEUTRAL;
      path.classList.toggle("neutral", !r.owner);
      path.classList.toggle("selected", id === selected);
      path.classList.toggle("target", targets.has(id) || plannedAttacksOn(id).length > 0);
      path.classList.toggle("capital-ok", capitalOk.has(id));
      path.classList.toggle("fought", (flashUntil.get(id) || 0) > now);
    }

    for (const g of svg.querySelectorAll(".badge")) {
      const id = Number(g.dataset.id);
      const r = region(id);
      g.querySelector(".count").textContent = r.castle;
      g.classList.toggle("unwalled", r.castle === 0);
      g.querySelector(".res-dot").style.fill = RES_COLOR[r.resource];
      g.querySelector(".castle-shape").style.stroke = r.owner ? colorOf(r.owner) : "rgba(255,255,255,.6)";
      g.querySelector(".star").setAttribute("visibility", r.capitalOf ? "visible" : "hidden");
      const bld = g.querySelector("g.bld");
      bld.setAttribute("visibility", r.building !== "None" ? "visible" : "hidden");
      bld.querySelector("text").textContent = BLD_LETTER[r.building] || "";
      g.querySelector(".name").setAttribute("visibility", showNames || id === selected ? "visible" : "hidden");
    }

    // Planned attacks.
    const layer = svg.querySelector(".orders");
    layer.innerHTML = orders.filter((o) => o.kind === "attack").map((o) => {
      const a = worldRegion(o.from).label, b = worldRegion(o.to).label;
      const mx = (a[0] + b[0]) / 2, my = (a[1] + b[1]) / 2 - Math.min(40, Math.hypot(b[0] - a[0], b[1] - a[1]) / 4);
      return `<path class="order-line attack w-${o.weapon.toLowerCase()}" d="M${a[0]},${a[1]} Q${mx},${my} ${b[0]},${b[1]}"/>`;
    }).join("");
  }

  // ---------- zoom & pan

  function setView(v) {
    const minW = 300;
    v.w = Math.max(minW, Math.min(world.width, v.w));
    v.h = v.w * world.height / world.width;
    v.x = Math.max(0, Math.min(world.width - v.w, v.x));
    v.y = Math.max(0, Math.min(world.height - v.h, v.y));
    view = v;
    $("#cq-map").setAttribute("viewBox", `${v.x.toFixed(1)} ${v.y.toFixed(1)} ${v.w.toFixed(1)} ${v.h.toFixed(1)}`);
    if (s) renderMap();
  }

  function zoomAt(factor, cx, cy) {
    const w = view.w * factor;
    const h = w * world.height / world.width;
    setView({ x: cx - (cx - view.x) * (w / view.w), y: cy - (cy - view.y) * (h / view.h), w, h });
  }

  function toMap(svg, clientX, clientY) {
    const rect = svg.getBoundingClientRect();
    return [view.x + (clientX - rect.left) / rect.width * view.w, view.y + (clientY - rect.top) / rect.height * view.h];
  }

  function attachMapInput(svg) {
    let drag = null;
    svg.onwheel = (ev) => {
      ev.preventDefault();
      const [x, y] = toMap(svg, ev.clientX, ev.clientY);
      zoomAt(ev.deltaY > 0 ? 1.15 : 1 / 1.15, x, y);
    };
    svg.onpointerdown = (ev) => {
      drag = { x: ev.clientX, y: ev.clientY, vx: view.x, vy: view.y, moved: false, target: ev.target };
      svg.setPointerCapture(ev.pointerId);
    };
    svg.onpointermove = (ev) => {
      if (!drag) return;
      const dx = ev.clientX - drag.x, dy = ev.clientY - drag.y;
      if (!drag.moved && Math.hypot(dx, dy) < 5) return;
      drag.moved = true;
      svg.classList.add("panning");
      const rect = svg.getBoundingClientRect();
      setView({ x: drag.vx - dx / rect.width * view.w, y: drag.vy - dy / rect.height * view.h, w: view.w, h: view.h });
    };
    svg.onpointerup = () => {
      if (drag && !drag.moved) {
        const path = drag.target.closest && drag.target.closest(".region");
        if (path) clickRegion(Number(path.dataset.id));
      }
      drag = null;
      svg.classList.remove("panning");
    };
    $("#cq-zoom-in").onclick = () => zoomAt(1 / 1.4, view.x + view.w / 2, view.y + view.h / 2);
    $("#cq-zoom-out").onclick = () => zoomAt(1.4, view.x + view.w / 2, view.y + view.h / 2);
    $("#cq-zoom-reset").onclick = () => setView({ x: 0, y: 0, w: world.width, h: world.height });
  }

  // ---------- clicks

  function clickRegion(id) {
    if (!s) return;

    if (s.phase === "CapitalPick" && isMe(s.currentPicker)) {
      if (s.validCapitals.includes(id)) invoke("ConquestPickCapital", id);
      else showError("Pick a highlighted region: unclaimed and not next to another capital.");
      return;
    }

    selected = id;
    render();
  }

  // ================================================================== panels

  function render() {
    if (!s || !world) return;
    renderTop();
    renderPlayers();
    renderMap();
    renderPanel();
    renderResult();
    renderFinished();
  }

  function renderTop() {
    const picker = s.currentPicker;
    const mine = isMe(picker);
    const you = (t) => `<span class="you">${t}</span>`;
    const me = myPlayer();
    const readyCount = s.players.filter((p) => !p.eliminated && p.ready).length;
    const alive = s.players.filter((p) => !p.eliminated).length;

    const text = {
      ColorQuestion: "Closest guess picks a colour first",
      ColorPick: mine ? you("Pick your colour") : `${who(picker)} is picking a colour`,
      CapitalPick: mine ? you("Click a glowing region for your capital") : `${who(picker)} is choosing a capital`,
      SupplyQuestion: "Supply question — 1st gets +2 of each, and strikes first",
      Planning: me && me.ready && !editing ? `Orders sent — ${readyCount}/${alive} ready` : you("Plan your moves"),
      BattleQuestion: "Battle question — win it to land your weapons",
      Finished: "The war is over",
    }[s.phase] || "";

    $("#cq-banner").innerHTML = text;
    $("#cq-round").textContent = s.round > 0 ? `Round ${s.round}/${s.maxRounds}` : "Setup";

    if (s.you) {
      const left = s.phase === "Planning" ? remaining() : s.you.wallet;
      const inc = s.you.income;
      $("#cq-wallet").innerHTML = ["gold", "manpower", "production"].map((k) =>
        `<span class="chip ${left[k] !== s.you.wallet[k] ? "spent" : ""}" title="${k}"><i class="res res-${k}"></i>${left[k]}<small>+${inc[k]}</small></span>`).join("");
    }
    $("#cq-leave").hidden = s.phase === "Finished";
  }

  function renderPlayers() {
    $("#cq-players").innerHTML = s.players.map((p) => {
      const color = p.color ? COLORS[p.color] : "var(--line)";
      const tags = (isMe(p.id) ? '<span class="tag">YOU</span>' : "") + (p.left ? '<span class="tag">LEFT · BOT</span>' : "");
      const ready = s.phase === "Planning" && p.ready && !p.eliminated ? '<span class="ready">✓ ready</span>' : "";
      const turn = p.id === s.currentPicker ? "turn" : "";
      return `
        <div class="player cq-player ${turn} ${p.eliminated ? "out" : ""}">
          <div class="swatch" style="background:${color}"></div>
          <div>
            <div class="p-name">${esc(p.name)}${tags}${ready}</div>
            <div class="p-meta"><span>${p.regions} regions</span>${p.capital ? `<span>★ ${esc(regionName(p.capital))}</span>` : ""}</div>
          </div>
          <div class="p-score" title="Score">${p.score}</div>
        </div>`;
    }).join("");
  }

  function renderPanel() {
    const panel = $("#cq-panel");
    if (s.question) return renderQuestion(panel);
    renderedQuestionKey = null;

    switch (s.phase) {
      case "ColorPick": return renderColorPick(panel);
      case "CapitalPick": return renderCapitalPick(panel);
      case "Planning": return renderPlanning(panel);
      default: panel.innerHTML = `<div class="hint">${s.phase === "Finished" ? "Game over." : "Waiting…"}</div>`;
    }
  }

  function renderColorPick(panel) {
    if (!isMe(s.currentPicker)) {
      panel.innerHTML = `<div class="panel-title">Colours</div><div class="hint">${who(s.currentPicker)} is picking…</div>`;
      return;
    }
    panel.innerHTML = `
      <div class="panel-title">Pick your colour</div>
      <div class="cq-colors">${rules.colors.map((c) =>
        `<button class="cq-color" data-color="${c}" style="background:${COLORS[c]}" ${s.availableColors.includes(c) ? "" : "disabled"}>${c}</button>`).join("")}
      </div>`;
    for (const b of panel.querySelectorAll(".cq-color")) b.onclick = () => invoke("ConquestPickColor", b.dataset.color);
  }

  function renderCapitalPick(panel) {
    panel.innerHTML = isMe(s.currentPicker)
      ? `<div class="panel-title">Your capital</div>
         <div class="hint">Click a glowing region on the map. Your capital starts with a level-2 castle and produces
         +1 of every resource each round. Capitals can't border each other.</div>`
      : `<div class="panel-title">Capitals</div><div class="hint">${who(s.currentPicker)} is choosing a capital…</div>`;
  }

  function regionCard(id) {
    const r = region(id);
    const wr = worldRegion(id);
    const owner = r.owner ? who(r.owner) : "Neutral";
    const bld = r.building !== "None" ? `<span>${esc(r.building)}</span>` : "";
    return `
      <div class="cq-region-head"><span class="cq-region-name">${esc(wr.name)}</span><span class="hint">${esc(wr.continent)}</span></div>
      <div class="cq-region-meta">
        <span>${owner}</span>
        <span><i class="res res-${RES_CLASS[r.resource]}"></i> ${esc(r.resource)}</span>
        <span>🏰 castle ${r.castle}</span>
        ${r.capitalOf ? `<span>★ capital of ${who(r.capitalOf)}</span>` : ""}${bld}
      </div>`;
  }

  function renderPlanning(panel) {
    const me = myPlayer();
    if (!s.you || !me || me.eliminated) {
      panel.innerHTML = `<div class="panel-title">Planning</div><div class="hint">You're out of the war. Watching…</div>`;
      return;
    }

    if (me.ready && !editing) {
      panel.innerHTML = `
        <div class="panel-title">Orders sent</div>
        <div class="hint">Waiting for the others to finish planning.</div>
        ${ordersList(false)}
        <div class="plan-footer"><button id="cq-edit" class="btn">Change orders</button></div>`;
      $("#cq-edit").onclick = () => { editing = true; render(); };
      return;
    }

    let body = `<div class="panel-title">Plan your moves</div>`;
    if (!selected) {
      body += `<div class="hint">Click one of your regions to fortify or build, or a neighbouring region to attack it.</div>`;
    } else {
      body += regionCard(selected);
      const r = region(selected);
      if (r.owner === playerId) body += ownRegionActions(selected);
      else if (myNeighbours(selected).length) body += attackActions(selected);
      else body += `<div class="hint">Not next to any of your regions, so you can't attack it yet.</div>`;
    }

    body += ordersList(true);
    const cost = plannedCost();
    const left = remaining();
    body += `
      <div class="plan-footer">
        <div class="plan-total"><span>Cost ${walletHtml(cost)}</span><span>Left ${walletHtml(left)}</span></div>
        <button id="cq-ready" class="btn primary">${orders.length ? `Ready — send ${orders.length} order${orders.length > 1 ? "s" : ""}` : "Ready — do nothing this round"}</button>
      </div>`;
    panel.innerHTML = body;
    wirePlanning(panel);
  }

  function ownRegionActions(id) {
    const r = region(id);
    const left = remaining();
    let html = `<div class="cq-actions">`;

    // Fortify
    if (r.castle >= rules.maxCastle) {
      html += `<div class="hint">The castle is at its highest level.</div>`;
    } else if (plannedFortify(id)) {
      html += `<div class="hint">Fortifying to level ${r.castle + 1} this round.</div>`;
    } else {
      const cost = rules.fortifyCosts[r.castle];
      html += `
        <div class="cq-row"><span class="label">Fortify</span>
          <button id="cq-fortify" class="btn small" ${covers(left, cost) ? "" : "disabled"}>Castle ${r.castle} → ${r.castle + 1}</button>
          <span class="cost">${walletHtml(cost)}</span>
        </div>`;
    }

    // Build
    if (r.building === "None" && !plannedBuildAt(id)) {
      html += `<div class="cq-row"><span class="label">Build</span></div><div class="cq-build">`;
      for (const b of rules.buildings) {
        const blocked = (b.building === "University" && hasUniversity()) || !covers(left, b.cost);
        html += `<button class="btn" data-build="${b.building}" ${blocked ? "disabled" : ""} title="${esc(b.effect)}">
          ${esc(b.building)} <small>${walletHtml(b.cost)} · ${esc(b.effect)}</small></button>`;
      }
      html += `</div>`;
    } else if (plannedBuildAt(id)) {
      html += `<div class="hint">Building a ${esc(plannedBuildAt(id).building)} here this round.</div>`;
    }

    html += `<div class="hint">Click a glowing neighbour to attack it.</div>`;
    return html + `</div>`;
  }

  const WEAPON_ICON = { Sword: "🗡️", Catapult: "🪨", Trebuchet: "🏹" };

  function attackActions(id) {
    const r = region(id);
    const left = remaining();
    const after = castleAfterPlanned(id);
    const planned = plannedAttacksOn(id);
    const defence = r.owner
      ? `${who(r.owner)} answers too: your weapon lands if you're right and they aren't, or you're both right and you're faster.`
      : "Neutral: your weapon lands if you answer the battle question right.";

    let html = `<div class="cq-actions"><div class="hint">${defence}</div>`;
    if (planned.length) {
      html += `<div class="hint">Already sending ${planned.map((a) => esc(a.weapon)).join(" + ")} — ${after < 0 ? "enough to capture if they land" : `castle would drop to ${after}`}.</div>`;
    }
    if (after < 0) return html + `</div>`;

    html += `<div class="cq-build">`;
    for (const w of rules.weapons) {
      const effect = w.power > after ? "captures" : `castle −${w.power}`;
      html += `<button class="btn" data-weapon="${w.weapon}" ${covers(left, w.cost) ? "" : "disabled"}>
        ${WEAPON_ICON[w.weapon] || ""} ${esc(w.weapon)} · power ${w.power}
        <small>${walletHtml(w.cost)} · <b>${effect}</b></small></button>`;
    }
    return html + `</div></div>`;
  }

  function ordersList(removable) {
    if (!orders.length) return `<ul class="orders"></ul>`;
    return `<ul class="orders">${orders.map((o, i) => {
      let text;
      if (o.kind === "attack") text = `${WEAPON_ICON[o.weapon] || ""} ${esc(o.weapon)} → ${esc(regionName(o.to))}`;
      else if (o.kind === "build") text = `Build ${esc(o.building)} in ${esc(regionName(o.region))}`;
      else text = `Fortify ${esc(regionName(o.region))} to level ${region(o.region).castle + 1}`;
      return `<li><span>${text}</span>${removable ? `<button data-remove="${i}" aria-label="Remove">✕</button>` : ""}</li>`;
    }).join("")}</ul>`;
  }

  function wirePlanning(panel) {
    const fortify = panel.querySelector("#cq-fortify");
    if (fortify) fortify.onclick = () => { orders.push({ kind: "fortify", region: selected }); render(); };
    for (const b of panel.querySelectorAll("[data-build]")) {
      b.onclick = () => { orders.push({ kind: "build", region: selected, building: b.dataset.build }); render(); };
    }
    for (const b of panel.querySelectorAll("[data-weapon]")) {
      b.onclick = () => {
        const sources = myNeighbours(selected);
        if (!sources.length) return;
        const capital = myPlayer().capital;
        const from = sources.includes(capital) ? capital : sources[0];
        orders.push({ kind: "attack", from, to: selected, weapon: b.dataset.weapon });
        render();
      };
    }
    for (const b of panel.querySelectorAll("[data-remove]")) {
      b.onclick = () => { orders.splice(Number(b.dataset.remove), 1); render(); };
    }
    const ready = panel.querySelector("#cq-ready");
    if (ready) ready.onclick = async () => {
      ready.disabled = true;
      try {
        await conn.invoke("ConquestSubmitPlan", orders);
        editing = false;
        render();
      } catch (e) {
        showError(e);
        ready.disabled = false;
      }
    };
  }

  // ---------- questions

  function renderQuestion(panel) {
    const q = s.question;
    const meAnswering = s.pendingAnswerers.includes(playerId);
    const meIn = s.answerers.includes(playerId);
    const key = `${q.id}|${s.questionDeadline}|${meAnswering}`;
    if (key === renderedQuestionKey) return;
    renderedQuestionKey = key;

    const title = {
      ColorQuestion: "Closest guess picks a colour first",
      SupplyQuestion: "Supplies: 1st +2 of each, 2nd +1",
      BattleQuestion: "Battle! Win this to land your weapons",
    }[s.phase] || "Question";
    const forWhom = s.answerers.length === s.players.filter((p) => !p.eliminated).length
      ? "Everyone" : s.answerers.map((a) => (isMe(a) ? "You" : esc(player(a).name))).join(", ");
    const removed = s.you ? s.you.removedOption : null;

    let body;
    if (meAnswering && q.kind === "MultipleChoice") {
      body = `<div class="options">${q.options.map((o, i) => i === removed ? "" :
        `<button class="btn option" data-i="${i}">${esc(o)}</button>`).join("")}</div>
        ${removed != null ? `<div class="hint">Your University removed one wrong answer.</div>` : ""}`;
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
      body = `<div class="locked">${forWhom} ${s.answerers.length > 1 ? "are" : "is"} answering. You're watching this one.</div>`;
    }

    panel.innerHTML = `
      <div class="panel-title">${title}</div>
      <div class="q-head"><span class="q-cat">${esc(q.category)}</span><span class="q-who">${forWhom}</span></div>
      <div class="q-text">${esc(q.text)}</div>
      ${body}`;

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
    const panel = $("#cq-result");
    if (!lastResult || s.question || s.phase === "Finished") { panel.hidden = true; return; }
    const q = lastResult.question;
    const rows = lastResult.results.map((r) => {
      let mark = '<span class="bad">✕</span>', detail = "no answer";
      if (r.given && r.given.kind === "choice") {
        mark = r.correct ? '<span class="ok">✓</span>' : '<span class="bad">✕</span>';
        detail = esc(q && q.options ? q.options[r.given.optionIndex] : "?");
      } else if (r.given && r.given.kind === "numeric") {
        mark = r.correct ? '<span class="ok">✓</span>' : '<span class="t">≈</span>';
        detail = `${fmt(r.given.value)} <span class="t">(off by ${fmt(r.error)})</span>`;
      }
      return `<li>${mark}<span>${who(r.player)}: ${detail}</span>${r.elapsed ? `<span class="t">${seconds(r.elapsed)}s</span>` : ""}</li>`;
    }).join("");
    panel.innerHTML = `
      <div class="panel-title">Last question</div>
      ${q ? `<div class="q-text" style="font-size:14px;margin-top:0">${esc(q.text)}</div>` : ""}
      <div class="r-answer">Answer: <b>${esc(lastResult.correctAnswer)}</b></div>
      <ul class="r-list">${rows}</ul>`;
    panel.hidden = false;
  }

  function renderFinished() {
    const overlay = $("#cq-finished");
    if (s.phase !== "Finished" || !s.standings) { overlay.hidden = true; return; }
    const mine = s.standings.find((x) => isMe(x.player));
    $("#cq-finish-title").textContent = mine && mine.place === 1 ? "Victory!" : "The war is over";
    $("#cq-standings").innerHTML = s.standings.map((x) => `
      <li><span class="place">${x.place}</span>
        <span>${who(x.player)} <span class="t">${x.regions} regions${x.eliminated ? ", wiped out" : ""}</span></span>
        <b>${x.score}</b></li>`).join("");
    overlay.hidden = false;
    $("#cq-confirm-leave").hidden = true;
  }

  // ================================================================== clock

  function tick() {
    if (active && s) {
      const deadline = s.questionDeadline || s.pickDeadline || s.planDeadline;
      const bar = $("#cq-clock-bar");
      if (deadline) {
        const total = s.questionDeadline ? (s.question && s.question.kind === "Numeric" ? 20000 : 15000)
          : s.pickDeadline ? 20000 : 60000;
        const left = Date.parse(deadline) - (Date.now() + clockOffset);
        const frac = Math.max(0, Math.min(1, left / total));
        bar.style.width = "100%";
        bar.style.transform = `scaleX(${frac})`;
        bar.parentElement.classList.toggle("low", frac < 0.25);
      } else {
        bar.style.width = "0";
      }
      // Stop flashing regions once their time is up.
      const now = Date.now();
      for (const [id, until] of flashUntil) if (until < now) { flashUntil.delete(id); renderMap(); }
    }
    requestAnimationFrame(tick);
  }
  requestAnimationFrame(tick);

  // ================================================================== wiring

  function wire() {
    $("#cq-leave").onclick = () => { $("#cq-confirm-leave").hidden = false; };
    $("#cq-stay").onclick = () => { $("#cq-confirm-leave").hidden = true; };
    $("#cq-confirm-leave-btn").onclick = () => { invoke("LeaveMatch"); goToLobby(); };
    $("#cq-lobby").onclick = () => { invoke("LeaveMatch"); goToLobby(); };
  }

  // `debug` lets automated browser tests read state and click regions by id (map shapes are irregular).
  return { joined, updated, reset, wire, loadWorld, debug: { state: () => s, click: (id) => clickRegion(id) } };
})();
