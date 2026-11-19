# Quiz Cards (Triviador)

Uno with questions, built on Azure free tiers as AI-200 practice. Match the colour, answer the card's question to
play it, steal when others are slow. Rules: [docs/CARDS.md](docs/CARDS.md).

The repo started as a Triviador-style map game; its Quick match and Conquest modes are still in the code
(`Domain/Matches`, `Domain/Conquest`) but the app only offers Quiz Cards now.

## Run it

```
dotnet run --project src/Triviador.Server
```

Open http://localhost:5080 and press **Play vs bots**. To test with real players, open more private/incognito
windows (each one is a separate player) and press **Find players** in each, or use **Invite friends**.
Nobody gets bots automatically: anyone waiting can press **Add bots and start** to fill the empty seats
(set `Game:BotFillAfterSeconds` above 0 to auto-fill after that many seconds instead).

Handy overrides while developing (fast bots, short waits):

```
dotnet run --project src/Triviador.Server -- --Game:BotSpeedFactor=0.2 --Game:BotFillAfterSeconds=3
```

On Windows you can also just double-click `run.cmd`.

## Conquest mode

World-map strategy for 2–4 players (bots fill empty seats): resources, buildings, simultaneous turns and
quiz-powered battles. Rules: [docs/CONQUEST.md](docs/CONQUEST.md). Start it from the lobby with **Conquest vs bots**,
or choose *Conquest* in a private game. The map is generated from Natural Earth borders by `tools/worldmap`.

## Quiz Cards mode

Uno with questions, for 2–4 players (bots fill empty seats): categories replace colours, and every card you
play is a multiple-choice question with a 3-second head start before anyone can steal it. Rules and balance
notes: [docs/CARDS.md](docs/CARDS.md). Start it from the lobby with **Play vs bots**, **Find players** (public queue) or **Invite friends**
(a private game with a share link, already set to Quiz Cards). The card table fills the window; **F** toggles full screen.

## Questions

300 multiple-choice and 55 number questions. The first ones are written out in
`src/Triviador.Domain/Questions/SampleQuestions.cs`; the rest are in
`src/Triviador.Domain/Questions/question-bank.json` (one question per line, embedded in the build).
To add questions, add lines to the JSON with a new unique `id`. Quiz Cards sorts them into its four
colours by `category` (Geography, Science, Tech/Azure/.NET/Web, everything else → History).

## Deploy to Azure

See [DEPLOY.md](DEPLOY.md): Azure Container Apps on the free tier, deployed by GitHub Actions
(`.github/workflows/deploy.yml`) from Bicep templates in `infra/`.

## Milestone 2 — server and browser client

```
src/Triviador.Server
  Hubs/GameHub.cs          SignalR hub: one method per player action, rule errors → HubException
  Hubs/PlayerIdProvider.cs dev identity from ?playerId= (replaced by Entra External ID later)
  Rooms/MatchRoom.cs       hosts one Match: per-room async lock, bots with thinking time,
                           AFK takeover after TurnTimeoutSeconds, broadcasts events + full state
  Rooms/MatchRegistry.cs   all live matches (in memory → single replica for now)
  Rooms/Matchmaker.cs      queue: 3 humans start together, otherwise bots fill after a wait
  Rooms/GameLoop.cs        BackgroundService ticking every 200 ms
  Contracts/Dtos.cs        everything the browser receives (never contains unrevealed answers)
  wwwroot/                 plain HTML/CSS/JS client, SVG hex map, SignalR JS client in lib/
```

Server → client messages: `MatchJoined(map, state)`, `MatchUpdated(events, state)`, `QueueStatus(status)`.
Client → server: `PlayVsBots`, `FindMatch`, `CancelSearch`, `LeaveMatch`, `PlaceCastle`, `ClaimTerritory`,
`Attack`, `AnswerChoice`, `AnswerNumber`.

Refreshing the page mid-match reconnects you to the same match. A player who stops responding has
their turns played by a bot; their unanswered questions simply expire.

## Milestone 1 — game engine

Pure C#, no packages, no network. Everything else (SignalR, Cosmos, Azure) plugs in around it.

```
src/Triviador.Domain
  Map/        GameMap (validated, connected graph), Maps.Valley() — 15 territories, 3x5 hex grid
  Questions/  Question / PublicQuestion (client-safe, no answer), IQuestionSource, sample bank
  Matches/    Match state machine, MatchOptions, MatchEvent types, MatchSnapshot
  Ranking/    EloRating — 3-player placement scored as pairwise games
  Bots/       BotPlayer — random legal moves, configurable accuracy
tests/Triviador.Domain.Tests   xUnit
```

## Rules as implemented

1. **Castles** — seats 1→3 each place a castle (3 HP) on a free territory not bordering another castle.
2. **Expansion** — numeric "closest guess" question to all. Closest (then fastest) claims 2 territories,
   runner-up claims 1. Claims must border your land if any free bordering territory exists.
   Repeats until the map is full. Non-answerers get nothing.
3. **Battle** — N rounds (default 4); attack order rotates each round. Attack a bordering enemy territory →
   multiple-choice duel between attacker and defender. Exactly one right wins; both right or both wrong
   → numeric tiebreak (closest, then fastest; nobody answers → defender holds).
   Winning on a plain territory takes it. Winning on a castle removes 1 HP; at 0 the owner is eliminated
   and all their land goes to the attacker.
4. **End** — after the last round or when one player remains. Placement: alive players by score
   (sum of territory values), then castle HP; eliminated players below, later knock-outs ranked higher.

## Design notes for the server (milestone 2)

- `Match` is single-threaded: the host must serialize calls per match (one lock or a channel per match).
- Time comes from `TimeProvider`; the server measures answer speed. Call `Tick()` from a timer.
- After every command, `DequeueEvents()` and broadcast. Clients only ever get `PublicQuestion`.
- `GetSnapshot()` is for reconnecting clients.
- Rule violations throw `GameRuleException` → return as a hub error.

## Run

```
dotnet test
```
