"use strict";

// Quiz Cards screen. Uses helpers from app.js at call time: conn, playerId, $, esc, invoke, showError,
// enteringGame, goToLobby.

const Cards = (() => {
  const CAT = {
    Geography: { color: "#3fbf7f", icon: "🌍" },
    Science: { color: "#3e9bff", icon: "🔬" },
    History: { color: "#f5b942", icon: "🏛️" },
    Tech: { color: "#a77bf3", icon: "💻" },
  };
  const KIND = {
    Lockout: { sym: "🔒", name: "Lockout", tip: "Nobody can steal the next player's question." },
    HotSeat: { sym: "⇄", name: "Hot Seat", tip: "Reverse direction. The next player gets no head start." },
    PopQuiz: { sym: "?!", name: "Pop Quiz", tip: "The next player must answer a question. Wrong or too slow: draw 2." },
    PickTopic: { sym: "◆", name: "Pick Your Topic", tip: "Wild: you choose the next category." },
    Duel: { sym: "⚔", name: "Duel", tip: "Wild: you and an opponent answer the same question. Loser draws 4 (both right: the slower one)." },
  };
  const SEAT_COLORS = ["#e5484d", "#3e9bff", "#f5b942", "#3fbf7f"];
  const MIN_REVEAL_MS = 1800;

  let rules = null;
  let s = null;                 // CardsStateDto
  let active = false;
  let clockOffset = 0;
  let reveal = null;            // { question, owner, purpose, correctIndex, results, until }
  let picked = null;            // { serial, index }
  let choosing = null;          // { card, category, opponent } while picking a wild's options
  let renderedQuestionKey = null;

  // For entry animations: what was on screen last time.
  let lastTopId = null;
  let lastHandIds = null;
  let lastCounts = new Map();

  // ================================================================== lifecycle

  function joined(r, state) {
    rules = r;
    enteringGame();
    active = true;
    s = null;
    reveal = null;
    picked = null;
    choosing = null;
    renderedQuestionKey = null;
    lastTopId = null;
    lastHandIds = null;
    lastCounts = new Map();
    $("#k-log").innerHTML = "";
    $("#lobby").hidden = true;
    $("#game").hidden = true;
    $("#conquest").hidden = true;
    $("#cards").hidden = false;
    $("#k-finished").hidden = true;
    $("#k-confirm-leave").hidden = true;
    log(`Quiz Cards! Match the <b>category</b> or number. Every card is a question — first ${rules.headStartSeconds}s are yours, then anyone can steal.`, "round-line");
    apply(state);
  }

  function updated(events, state) {
    if (!active || !s || state.gameId !== s.gameId) return;
    const before = s;
    for (const e of events) handle(e.type, e.data, before);
    apply(state);
  }

  function reset() {
    active = false;
    s = null;
    $("#cards").hidden = true;
    if (document.fullscreenElement) document.exitFullscreen().catch(() => {});
  }

  function toggleFullscreen() {
    if (document.fullscreenElement) document.exitFullscreen().catch(() => {});
    else document.documentElement.requestFullscreen().catch(() => showError("Your browser blocked full screen."));
  }

  function syncFullscreenButton() {
    const on = !!document.fullscreenElement;
    $("#k-fullscreen").textContent = on ? "⤢ Exit full screen" : "⛶ Full screen";
    document.body.classList.toggle("is-fullscreen", on);
  }

  function apply(state) {
    s = state;
    clockOffset = Date.parse(state.serverNow) - Date.now();
    if (choosing && (s.phase !== "Turn" || s.currentPlayer !== playerId || !s.hand.some((c) => c.id === choosing.card.id))) choosing = null;
    render();
  }

  // ================================================================== helpers

  const now = () => Date.now() + clockOffset;
  const isMe = (id) => id === playerId;
  const player = (id) => s && s.players.find((p) => p.id === id);
  const nameOf = (id) => (isMe(id) ? "You" : (player(id) || { name: "?" }).name);
  const who = (id) => {
    const p = player(id);
    const color = p ? SEAT_COLORS[p.seat % SEAT_COLORS.length] : "#ccc";
    return `<b style="color:${color}">${esc(nameOf(id))}</b>`;
  };
  /** "You draw" / "Nino draws". */
  const verb = (id, you, other) => (isMe(id) ? you : other);
  /** "Your" / "your" / "Nino's". */
  const poss = (id, lower = false) => {
    if (!isMe(id)) return `${who(id)}'s`;
    const p = player(id);
    const color = p ? SEAT_COLORS[p.seat % SEAT_COLORS.length] : "#ccc";
    return `<b style="color:${color}">${lower ? "your" : "Your"}</b>`;
  };
  const catLabel = (c) => `<span class="k-cat" style="--cat:${CAT[c].color}">${CAT[c].icon} ${esc(c)}</span>`;

  function cardName(card) {
    if (card.kind === "Number") return `${card.category} ${card.number}`;
    return card.category ? `${card.category} ${KIND[card.kind].name}` : KIND[card.kind].name;
  }

  function cardHtml(card, extra = "", style = "") {
    const wild = !card.category;
    const color = `style="${wild ? "" : `--cat:${CAT[card.category].color};`}${style}"`;
    const face = card.kind === "Number" ? card.number : KIND[card.kind].sym;
    const corner = wild ? "★" : CAT[card.category].icon;
    const label = card.kind === "Number" ? esc(card.category) : esc(KIND[card.kind].name);
    const tip = card.kind === "Number" ? `${card.category} ${card.number}` : `${KIND[card.kind].name}: ${KIND[card.kind].tip}`;
    return `<div class="k-card ${wild ? "wild" : ""} kind-${card.kind} ${extra}" ${color} data-id="${card.id}" title="${esc(tip)}">
      <span class="k-corner tl">${corner}</span>
      <span class="k-face">${face}</span>
      <span class="k-label">${label}</span>
      <span class="k-corner br">${corner}</span>
    </div>`;
  }

  // ================================================================== events → log

  function handle(type, d, before) {
    switch (type) {
      case "CardsTurnStarted":
        break;
      case "CardsQuestionOpened":
        if (d.purpose === "PopQuiz") log(`🎯 Pop Quiz for ${who(d.target)} — ${catLabel(d.category)}`);
        else if (d.purpose === "Duel") log(`⚔ ${who(d.owner)} ${verb(d.owner, "challenge", "challenges")} ${who(d.target)} to a Duel in ${catLabel(d.category)}`);
        else log(`${who(d.owner)} ${verb(d.owner, "play", "plays")} <b>${esc(cardName(d.card))}</b>${d.card.category ? "" : ` → ${catLabel(d.category)}`}`);
        break;
      case "CardsQuestionResolved": {
        const q = before && before.question;
        reveal = q ? {
          question: q.question, owner: q.owner, target: q.target, purpose: q.purpose,
          correctIndex: d.correctIndex, results: d.results,
          until: Date.now() + Math.max(MIN_REVEAL_MS, (rules.revealSeconds || 0) * 1000),
        } : null;
        break;
      }
      case "CardsCardLanded":
        if (d.stolenBy) log(`🫳 ${who(d.stolenBy)} <b>${verb(d.stolenBy, "steal", "steals")}</b> ${poss(d.owner, true)} card!`, "k-steal");
        else if (d.fizzled) log(`✗ ${who(d.owner)} got it wrong — card spent without its effect`);
        else log(`✓ ${who(d.owner)} ${verb(d.owner, "land", "lands")} it`);
        break;
      case "CardsCardReturned":
        log(`⌛ Nobody answered — ${who(d.owner)} ${verb(d.owner, "take", "takes")} the card back`);
        break;
      case "CardsStealDiscard":
        log(`${who(d.stealer)} ${verb(d.stealer, "discard", "discards")} a random card (${esc(cardName(d.card))})`);
        break;
      case "CardsDrew": {
        if (!d.count) break;
        const why = {
          Draw: "", TurnTimeout: " (out of time)", WrongAnswer: " (wrong answer)", TooSlow: " (too slow — stolen)",
          NoAnswer: " (no answer)", WrongSteal: " (wrong steal)", PopQuiz: " (pop quiz)", Duel: " (lost the duel)",
        }[d.reason] || "";
        log(`${who(d.player)} ${verb(d.player, "draw", "draws")} ${d.count}${why}`);
        break;
      }
      case "CardsLockout":
        log(`🔒 ${poss(d.player)} next question can't be stolen`);
        break;
      case "CardsReversed":
        log(`⇄ Direction reversed — ${who(d.hotSeat)} ${verb(d.hotSeat, "are", "is")} in the hot seat (no head start)`);
        break;
      case "CardsPopQuizResult":
        log(d.correct ? `${who(d.target)} ${verb(d.target, "survive", "survives")} the pop quiz` : `${who(d.target)} ${verb(d.target, "fail", "fails")} the pop quiz`);
        break;
      case "CardsDuelResult":
        log(d.loser ? `⚔ ${who(d.loser)} ${verb(d.loser, "lose", "loses")} the duel (${esc(d.reason)})` : "⚔ Both wrong — nobody draws");
        break;
      case "CardsFinished":
        log("Game over!", "round-line");
        break;
    }
  }

  function log(html, cls) {
    const li = document.createElement("li");
    li.innerHTML = html;
    if (cls) li.className = cls;
    const list = $("#k-log");
    list.appendChild(li);
    while (list.children.length > 80) list.firstChild.remove();
    list.scrollTop = list.scrollHeight;
  }

  // ================================================================== rendering

  function render() {
    if (!s) return;
    renderBanner();
    renderPlayers();
    renderTable();
    renderQuestion();
    renderHand();
    renderFinished();
    $("#k-round").textContent = `Turn ${Math.min(s.turn, s.maxTurns)}/${s.maxTurns}`;
  }

  function renderBanner() {
    let text;
    const q = s.question;
    if (s.phase === "Finished") text = "Game over";
    else if (q && q.purpose === "PopQuiz") text = isMe(q.target) ? `<span class="you">Pop Quiz!</span> Answer or draw ${rules.popQuizDraw}` : `Pop Quiz for ${who(q.target)}`;
    else if (q && q.purpose === "Duel") text = `Duel: ${who(q.owner)} vs ${who(q.target)}`;
    else if (q) text = isMe(q.owner) ? `<span class="you">Answer to land your card!</span>` : `${who(q.owner)} is answering… steal after ${rules.headStartSeconds}s`;
    else if (s.phase === "Reveal") text = isMe(s.currentPlayer) ? `<span class="you">You're up next…</span>` : `Up next: ${who(s.currentPlayer)}`;
    else if (isMe(s.currentPlayer)) text = `<span class="you">Your turn</span> — play a card or draw`;
    else text = `${who(s.currentPlayer)} is choosing a card…`;
    $("#k-banner").innerHTML = text;
  }

  function renderPlayers() {
    $("#k-players").innerHTML = s.players.map((p) => {
      const turn = s.currentPlayer === p.id && s.phase !== "Finished";
      const badges = [
        p.lockedOut ? `<span class="k-badge" title="Can't be stolen from next turn">🔒</span>` : "",
        p.hotSeat ? `<span class="k-badge" title="No head start next question">🔥</span>` : "",
        s.pendingPopQuiz === p.id ? `<span class="k-badge" title="Pop quiz coming">?!</span>` : "",
      ].join("");
      const tags = [isMe(p.id) ? "YOU" : "", p.left ? "BOT PLAYS" : ""].filter(Boolean).map((t) => `<span class="tag">${t}</span>`).join("");
      const backs = Array.from({ length: Math.min(p.cards, 12) }, () => "<i></i>").join("");
      return `<div class="player k-player ${turn ? "turn" : ""} ${p.left ? "left" : ""}">
        <span class="swatch" style="background:${SEAT_COLORS[p.seat % SEAT_COLORS.length]}"></span>
        <div>
          <div class="p-name">${esc(p.name)}${tags} ${badges}</div>
          <div class="k-backs">${backs}</div>
        </div>
        <div class="p-score ${lastCounts.has(p.id) && lastCounts.get(p.id) !== p.cards ? (p.cards > lastCounts.get(p.id) ? "k-bump up" : "k-bump down") : ""}" title="Cards in hand">${p.cards}</div>
      </div>`;
    }).join("");
    lastCounts = new Map(s.players.map((p) => [p.id, p.cards]));
  }

  function renderTable() {
    const myTurn = s.phase === "Turn" && isMe(s.currentPlayer);
    const topId = s.topCard ? s.topCard.id : null;
    if (topId !== lastTopId) {
      $("#k-top").innerHTML = s.topCard ? cardHtml(s.topCard, `big ${lastTopId === null ? "" : "k-enter"}`) : "";
      lastTopId = topId;
    }
    $("#k-now").innerHTML = `Now: ${catLabel(s.activeCategory)}`;
    $("#k-dir").textContent = s.direction > 0 ? "↻" : "↺";
    $("#k-dir").title = s.direction > 0 ? "Clockwise (seat order)" : "Reversed";
    const pile = $("#k-pile");
    pile.querySelector(".k-count").textContent = s.drawPile;
    pile.classList.toggle("can", myTurn);
    $("#k-draw").disabled = !myTurn;
  }

  function renderQuestion() {
    const panel = $("#k-question");
    const q = s.question;
    if (!q) {
      if (reveal && (Date.now() < reveal.until || s.phase === "Reveal")) return renderReveal(panel);
      panel.hidden = true;
      renderedQuestionKey = null;
      return;
    }

    reveal = null;
    panel.hidden = false;
    const key = `${q.serial}`;
    if (picked && picked.serial !== q.serial) picked = null;

    if (renderedQuestionKey !== key) {
      renderedQuestionKey = key;
      const head = q.purpose === "PopQuiz" ? `Pop Quiz for ${who(q.target)}`
        : q.purpose === "Duel" ? `Duel: ${who(q.owner)} vs ${who(q.target)}`
        : `${poss(q.owner)} card`;
      panel.classList.remove("k-pop");
      void panel.offsetWidth; // restart the entry animation
      panel.classList.add("k-pop");
      panel.innerHTML = `
        <div class="q-head">${catLabel(q.category)}<span class="q-who">${head}</span></div>
        <div class="q-text">${esc(q.question.text)}</div>
        <div class="k-timer"><div class="k-timer-bar"></div><div class="k-headstart"></div></div>
        <div class="k-status"></div>
        <div class="options">${q.question.options.map((o, i) => `<button class="btn option" data-i="${i}">${esc(o)}</button>`).join("")}</div>`;
      for (const b of panel.querySelectorAll(".option")) {
        b.onclick = () => {
          if (picked) return;
          picked = { serial: q.serial, index: Number(b.dataset.i) };
          tick();
          invoke("AnswerChoice", picked.index);
        };
      }
      const total = Date.parse(q.deadline) - Date.parse(q.askedAt);
      const headMs = Date.parse(q.stealOpensAt) - Date.parse(q.askedAt);
      panel.querySelector(".k-headstart").style.width = `${Math.max(0, (headMs / total) * 100)}%`;
      panel.querySelector(".k-headstart").hidden = q.purpose !== "Question" || headMs <= 0;
    }
    tick();
  }

  function renderReveal(panel) {
    const r = reveal;
    panel.hidden = false;
    renderedQuestionKey = null;
    const byPlayer = new Map(r.results.map((x) => [x.player, x]));
    const rows = r.results.map((x) => {
      const ok = x.correct ? '<span class="ok">✓</span>' : '<span class="bad">✗</span>';
      const t = x.elapsed ? ` <span class="t">${secondsOf(x.elapsed).toFixed(1)}s</span>` : "";
      const role = r.purpose === "Question" && x.player !== r.owner ? " (steal)" : "";
      return `<li>${ok} ${who(x.player)}${role}${x.given ? "" : " — no answer"}${t}</li>`;
    }).join("");
    panel.innerHTML = `
      <div class="q-text">${esc(r.question.text)}</div>
      <div class="options">${r.question.options.map((o, i) => {
        const mine = byPlayer.get(playerId);
        const cls = i === r.correctIndex ? "right" : mine && mine.given && mine.given.optionIndex === i ? "wrong" : "";
        return `<button class="btn option ${cls}" disabled>${esc(o)}</button>`;
      }).join("")}</div>
      <ul class="r-list">${rows}</ul>`;
    clearTimeout(renderReveal.timer);
    renderReveal.timer = setTimeout(() => { if (s && !s.question && s.phase !== "Reveal") renderQuestion(); }, Math.max(0, r.until - Date.now()) + 30);
  }

  function renderHand() {
    const myTurn = s.phase === "Turn" && isMe(s.currentPlayer);
    const playable = new Set(s.playable);
    const hand = [...s.hand].sort((a, b) => order(a) - order(b));
    const fresh = (c) => lastHandIds !== null && !lastHandIds.has(c.id);
    let delay = 0;
    $("#k-hand").innerHTML = hand.map((c) => {
      const isNew = fresh(c);
      // Several drawn cards arrive one after another.
      const style = isNew ? `animation-delay:${(delay++) * 120}ms` : "";
      return cardHtml(c, `${myTurn && playable.has(c.id) ? "playable" : myTurn ? "dim" : ""} ${choosing && choosing.card.id === c.id ? "chosen" : ""} ${isNew ? "k-new" : ""}`, style);
    }).join("") || `<div class="k-empty">No cards</div>`;
    lastHandIds = new Set(s.hand.map((c) => c.id));
    for (const el of $("#k-hand").querySelectorAll(".k-card.playable")) {
      el.onclick = () => playCard(s.hand.find((c) => c.id === Number(el.dataset.id)));
    }
    $("#k-hand-title").textContent = `Your hand · ${s.hand.length} card${s.hand.length === 1 ? "" : "s"}`;
    renderChooser();
  }

  function order(c) {
    const cats = ["Geography", "Science", "History", "Tech"];
    if (!c.category) return 1000 + (c.kind === "Duel" ? 1 : 0);
    return cats.indexOf(c.category) * 100 + (c.kind === "Number" ? c.number : 20 + Object.keys(KIND).indexOf(c.kind));
  }

  function playCard(card) {
    if (!card) return;
    if (card.category) {
      choosing = null;
      invoke("CardsPlay", card.id, null, null);
      return;
    }
    choosing = { card, category: null, opponent: null };
    renderHand();
  }

  function renderChooser() {
    const box = $("#k-chooser");
    if (!choosing) { box.hidden = true; return; }
    box.hidden = false;
    const c = choosing;
    const duel = c.card.kind === "Duel";
    const opponents = s.players.filter((p) => !isMe(p.id));
    box.innerHTML = `
      <div class="panel-title">${esc(KIND[c.card.kind].name)} — ${esc(KIND[c.card.kind].tip)}</div>
      <div class="k-choices">${Object.keys(CAT).map((k) =>
        `<button class="btn small k-choice ${c.category === k ? "on" : ""}" data-cat="${k}" style="--cat:${CAT[k].color}">${CAT[k].icon} ${k}</button>`).join("")}</div>
      ${duel ? `<div class="k-choices">${opponents.map((p) =>
        `<button class="btn small k-choice ${c.opponent === p.id ? "on" : ""}" data-opp="${esc(p.id)}">⚔ ${esc(p.name)} · ${p.cards}</button>`).join("")}</div>` : ""}
      <div class="private-actions">
        <button class="btn primary" id="k-confirm" ${c.category && (!duel || c.opponent) ? "" : "disabled"}>Play</button>
        <button class="btn ghost" id="k-cancel">Cancel</button>
      </div>`;
    for (const b of box.querySelectorAll("[data-cat]")) b.onclick = () => { c.category = b.dataset.cat; renderChooser(); };
    for (const b of box.querySelectorAll("[data-opp]")) b.onclick = () => { c.opponent = b.dataset.opp; renderChooser(); };
    box.querySelector("#k-cancel").onclick = () => { choosing = null; renderHand(); };
    box.querySelector("#k-confirm").onclick = () => {
      const { card, category, opponent } = c;
      choosing = null;
      invoke("CardsPlay", card.id, category, duel ? opponent : null);
      renderHand();
    };
  }

  function renderFinished() {
    const box = $("#k-finished");
    if (s.phase !== "Finished" || !s.standings) { box.hidden = true; return; }
    box.hidden = false;
    const me = s.standings.find((x) => isMe(x.player));
    $("#k-finish-title").textContent = me && me.place === 1 ? "You win!" : "Game over";
    $("#k-standings").innerHTML = s.standings.map((x) =>
      `<li><span class="place">${x.place}</span><span>${who(x.player)}</span><span>${x.cardsLeft} card${x.cardsLeft === 1 ? "" : "s"}</span></li>`).join("");
  }

  // ================================================================== timers (every frame)

  function secondsOf(span) {
    if (typeof span === "number") return span;
    const m = /^(?:(\d+)\.)?(\d+):(\d+):(\d+(?:\.\d+)?)$/.exec(span);
    return m ? (Number(m[1] || 0) * 86400 + Number(m[2]) * 3600 + Number(m[3]) * 60 + Number(m[4])) : 0;
  }

  function tick() {
    if (!active || !s) return;
    const t = now();

    // Turn clock in the top bar.
    const clock = $("#k-clock-bar");
    if (s.phase === "Turn" && s.turnDeadline) {
      const left = Math.max(0, Date.parse(s.turnDeadline) - t);
      clock.style.transform = `scaleX(${Math.min(1, left / (rules.turnSeconds * 1000))})`;
      clock.parentElement.classList.toggle("low", left < 5000);
    } else {
      clock.style.transform = "scaleX(0)";
    }

    const q = s.question;
    const panel = $("#k-question");
    if (!q || panel.hidden || renderedQuestionKey !== `${q.serial}`) return;

    const asked = Date.parse(q.askedAt), opens = Date.parse(q.stealOpensAt), deadline = Date.parse(q.deadline);
    const left = Math.max(0, deadline - t);
    const bar = panel.querySelector(".k-timer-bar");
    bar.style.transform = `scaleX(${left / (deadline - asked)})`;
    panel.querySelector(".k-timer").classList.toggle("low", left < 3000);

    const answered = q.answered.includes(playerId) || (picked && picked.serial === q.serial);
    const mine = q.purpose === "Question" ? isMe(q.owner) : isMe(q.owner) || isMe(q.target);
    const waitingHeadStart = q.purpose === "Question" && !mine && t < opens;
    const canClick = s.canAnswer && !answered && !waitingHeadStart && left > 0;

    for (const b of panel.querySelectorAll(".option")) {
      b.disabled = !canClick;
      b.classList.toggle("picked", !!picked && picked.serial === q.serial && picked.index === Number(b.dataset.i));
    }

    let status;
    if (answered) status = "Locked in — waiting for the result…";
    else if (q.purpose === "PopQuiz") status = isMe(q.target) ? `Pop Quiz! Wrong or too slow: draw ${rules.popQuizDraw}.` : "Only they can answer this one.";
    else if (q.purpose === "Duel") status = mine ? `Duel! Wrong — or slower if you're both right — draws ${rules.duelDraw}.` : "Only the duellists answer.";
    else if (mine) status = t < opens
      ? `<b>Head start: ${((opens - t) / 1000).toFixed(1)}s</b> — only you can answer`
      : q.stealable ? "<b>Hurry</b> — anyone can steal it now!" : "🔒 Locked out — nobody can steal this one";
    else if (!q.stealable) status = "🔒 Locked — no steals on this question";
    else if (waitingHeadStart) status = `Steal opens in <b>${((opens - t) / 1000).toFixed(1)}s</b>…`;
    else status = `<b>Steal!</b> Right: they draw ${rules.stolenDraw}, you discard 1 and take the turn. Wrong: draw ${rules.wrongStealDraw}.`;
    const el = panel.querySelector(".k-status");
    if (el.innerHTML !== status) el.innerHTML = status;
  }

  function loop() {
    tick();
    requestAnimationFrame(loop);
  }

  // ================================================================== wiring

  function wire() {
    $("#k-draw").onclick = () => invoke("CardsDraw");
    $("#k-pile").onclick = () => { if (s && s.phase === "Turn" && isMe(s.currentPlayer)) invoke("CardsDraw"); };
    $("#k-leave").onclick = () => { $("#k-confirm-leave").hidden = false; };
    $("#k-stay").onclick = () => { $("#k-confirm-leave").hidden = true; };
    $("#k-confirm-leave-btn").onclick = () => { invoke("LeaveMatch"); reset(); goToLobby(); };
    $("#k-lobby").onclick = () => { invoke("LeaveMatch"); reset(); goToLobby(); };
    $("#k-fullscreen").onclick = toggleFullscreen;
    document.addEventListener("fullscreenchange", syncFullscreenButton);
    $("#k-fullscreen").hidden = !document.documentElement.requestFullscreen;
    // Keyboard: F toggles full screen, 1–4 answer the open question.
    document.addEventListener("keydown", (e) => {
      if (!active || !s || e.target.tagName === "INPUT") return;
      if (e.key === "f" || e.key === "F") { toggleFullscreen(); return; }
      if (!s.question) return;
      const i = Number(e.key) - 1;
      const b = $("#k-question").querySelector(`.option[data-i="${i}"]`);
      if (b && !b.disabled) b.click();
    });
    requestAnimationFrame(loop);
  }

  return { joined, updated, reset, wire };
})();
