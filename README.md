# Triviador

A 3-player, map-based ranked quiz game (Triviador-style), built on Azure free tiers as AI-200 practice.

## Run it

```
dotnet run --project src/Triviador.Server
```

Open http://localhost:5080 and press **Play vs bots**. To test matchmaking with real players, open two more
private/incognito windows (each one is a separate player) and press **Find match** in all three.
A lone player in the queue gets bots after 12 seconds.

Handy overrides while developing (fast bots, short waits):

```
dotnet run --project src/Triviador.Server -- --Game:BotSpeedFactor=0.2 --Game:BotFillAfterSeconds=3
```

On Windows you can also just double-click `run.cmd`.

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
