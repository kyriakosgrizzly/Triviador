# Conquest — rules (v0.2: castles and weapons)

Catan × Risk × quiz on a real world map. 2–4 players (bots fill empty seats), about 25 minutes.
There are no troops: regions are defended by **castles**, and you attack with **weapons**. Winning the
battle question is what makes a weapon land. Every number lives in `ConquestOptions`.

## The map

- **30 regions** built from real country borders (Natural Earth), e.g. *Turkey & Caucasus*, *West Africa*, *Japan & Korea*.
- Regions border each other over land; **11 sea crossings** also count as borders (e.g. Brazil–West Africa, Bering Strait).
- Every region produces one resource. Each game deals them **randomly but spread out**:
  10 Gold, 10 Manpower, 10 Production, arranged so neighbouring regions share a resource as rarely as possible.
- Unclaimed regions are **neutral**; half start with a level-1 castle, the rest are unwalled.

## Setup

1. **Colour question.** Everyone guesses a number. Closest (then fastest) picks a colour first, then the runner-up, and so on.
   Colours: Crimson, Azure, Amber, Emerald, Violet, Copper.
2. **Capitals.** In the same order, each player picks a starting region (not next to another capital). It becomes their
   **capital** with a **level-2 castle**.
3. Everyone starts with **4 Gold, 4 Manpower, 4 Production**.

Picks have 20 seconds; if time runs out, one is made for you.

## A round (12 rounds at most)

| Step | What happens |
|---|---|
| 1. Supply question | Everyone guesses a number. **1st: +2 of each resource, 2nd: +1 of each** (2-player games: only 1st). The ranking is also this round's **initiative**: who strikes first. |
| 2. Income | Each region: **+1 of its resource**. Capital: **+1 of each**. Buildings add more. Last place on score gets **+1 of each**. |
| 3. Planning (60 s, all at once, hidden) | Buy weapons and choose targets, fortify castles, build. Press **Ready**. |
| 4. Battle question | If anyone attacks: everyone attacking or under attack answers one multiple-choice question. |
| 5. Resolution | Attacks happen in initiative order. |

## Castles

- Levels **0** (unwalled) to **3**. Raise a castle by **one level per round**:
  0→1: 1 Gold + 2 Production · 1→2: 1 Gold + 3 Production · 2→3: 2 Gold + 4 Production.
- Each castle level is worth 1 point.

## Weapons

| Weapon | Cost | Power |
|---|---|---|
| Sword | 1 Gold, 1 Manpower | 1 |
| Catapult | 2 Gold, 1 Manpower, 2 Production | 2 |
| Trebuchet | 3 Gold, 2 Manpower, 4 Production | 4 |

A weapon is bought for one attack and used up by it, hit or miss. You attack a region that borders one of yours.

## Battles

Your weapon **lands** if you win the battle question exchange:

- **Against a neutral region:** you answered right.
- **Against a player:** you were right and they weren't, **or both were right and you answered faster**.
  If you were wrong, or they were right and faster, the attack fails.

When a weapon lands:

- **Power above the castle level → the region is captured.** Its castle is destroyed (level 0).
- **Otherwise the castle loses that many levels.**

So a Sword takes an unwalled region and chips a castle; a Catapult takes level 0–1; a **Trebuchet takes even a full
level-3 castle in one hit**. Several attacks on the same region in one round add up: a Catapult then a Sword breaks a
level-2 castle.

If the region you attack *from* is lost earlier in the same round, your attack is cancelled and the weapon refunded.

## Buildings (one per region)

| Building | Cost | Effect |
|---|---|---|
| Barracks | 2 Gold, 2 Production | +1 Manpower per round |
| Workshop | 2 Gold, 2 Manpower | +1 Production per round |
| Market | 2 Manpower, 2 Production | +1 Gold per round |
| University | 4 Gold, 2 Production (one per player) | One wrong option removed from your battle question |

Captured buildings keep working for their new owner.

## Capitals and elimination

- Lose your capital and you lose its +1-of-each income. The captor scores it.
- A player with no regions left is eliminated.

## Winning

- **Score:** 2 per region, 1 per building, 1 per castle level, 3 per capital held (your own included).
- The game ends after **round 12**, or at the end of a round in which someone holds **18+ regions (60%)**,
  or when only one player has land left.
- Highest score wins; ties go to more regions, then better initiative in the last round.

## Balance (600 bot games, v0.2)

| Players | Avg rounds | Ended by domination | Winner's regions | Player-vs-player captures per game |
|---|---|---|---|---|
| 2 | 10.9 | 62% | 17 | 2.5 |
| 3 | 11.3 | 39% | 15 | 7.0 |
| 4 | 11.8 | 17% | 13 | 10.6 |
