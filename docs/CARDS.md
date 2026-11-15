# Quiz Cards

Uno with questions. 2–4 players, bots fill empty seats. Play vs bots, **Find players** (public queue, "Add bots and start" fills empty seats) or **Invite friends** (private game with a link). Engine: `src/Triviador.Domain/Cards/`,
host: `src/Triviador.Server/Rooms/CardsRoom.cs`, client: `src/Triviador.Server/wwwroot/cards.js`.

## Deck (104 cards)

Four categories instead of colours: **Geography, Science, History, Tech**. Per category: numbers 1–9 twice,
plus two each of Lockout, Hot Seat and Pop Quiz. Plus four Pick Your Topic and four Duel wilds.
Questions come from the multiple-choice bank; `CardDeck.CategoryOf` maps bank categories to card categories
(Azure/.NET/Web → Tech; Literature, Music… → History).

## A turn

1. Play a card that matches the pile's **category**, its **number** (or the same action), or a wild — or draw 1.
   Doing nothing for 20 s draws 1 for you.
2. The card's question opens with a 10 s timer. For the first **3 s only you** can answer.
   - Right: the card lands and its action happens.
   - Wrong: the card is spent (no action) and you **draw 2**.
3. After 3 s **anyone** can answer (first click locks in, one try each):
   - Right steal: your card still counts as played (its action is lost), **you draw 1**,
     the stealer **discards a random card** and **it becomes their turn**.
   - Wrong steal: the stealer **draws 2**; the question stays open.
4. Nobody right by the deadline: your card comes back and you draw 1.
5. After each question the right answer stays on screen for 2.5 s (0.8 s after a draw) before the next turn starts; the turn clock starts only then.

First empty hand wins (a steal discard can win too). After 120 turns, fewest cards wins.

## Action cards

| Card | Uno | Effect (only when the owner answers it right) |
|---|---|---|
| 🔒 Lockout | Skip | Nobody can steal the next player's question. |
| ⇄ Hot Seat | Reverse | Direction flips; the new next player gets no head start. |
| ?! Pop Quiz | +2 | The next player must answer a question first; wrong or too slow draws 2. No stacking (yet). |
| ◆ Pick Your Topic | Wild | Choose the category for the question and the pile. |
| ⚔ Duel | Wild +4 | Pick a category and an opponent; you both answer the same question. Wrong one draws 4; both right → the slower one; both wrong → nobody. |

## Balance notes

Everything is in `CardsOptions` (draw counts, timers, `HeadStart`, `RevealTime`, `DrawPause`, `MaxTurns`, `WrongAnswerReturnsCard`).
Bot simulations (3 players, 150 games each, turns until someone empties their hand):

| Players answer right | Wrong answer draws 2 | Wrong answer draws 1 |
|---|---|---|
| 65% | median 160 turns, often never ends | median 76 |
| 80% | median 62 | median 48 |
| 90% | median 38 | median 39 |

With "wrong draws 2", a wrong answer gains you a card overall (−1 played, +2 drawn), so hands only shrink if
players get roughly 80%+ right. That's why there's a 120-turn cap. If playtests drag, set
`WrongAnswerDraw = 1`. Taking the card back on a wrong answer (`WrongAnswerReturnsCard`) makes it much worse.

## Hub

Client → server: `PlayCardsVsBots(players)`, `FindCardsMatch()` (public queue: starts at once with 4 players; anyone waiting can call `StartQueueWithBots()` to fill the empty seats — no automatic bots unless `BotFillAfterSeconds` > 0), `CreatePrivateGameFor("cards", seats)` (invite link), `CardsPlay(cardId, category?, opponent?)`, `CardsDraw()`,
`AnswerChoice(index)`, `LeaveMatch()`, and `SetPrivateGameMode("cards", seats)` in private games.
Server → client: `CardsJoined(rules, state)`, `CardsUpdated(events, state)`. Each player's state contains only
their own hand; the open question never contains its answer.
