# Dustlife for other Meitou mods

Dustlife runs the factions' war off screen. Another mod can read it, and tell its own war stories around it, through `Dustlife.DustlifeApi`
(version 2): a public static class read by reflection, so no mod depends on Dustlife. Plain types only; nothing throws; before a war runs
(or with Dustlife off) reads come back empty. Read it from your own system's serial step (`Inputs`, `Schedule`, `SlowWorld`).

```csharp
var api = Type.GetType("Dustlife.DustlifeApi, Dustlife");   // null: Dustlife is not installed; carry on without it
bool active = (bool)api!.GetProperty("Active")!.GetValue(null)!;
```

| Member | What it gives |
| --- | --- |
| `int Version` (const) | 2. Only grows; nothing is removed or changes meaning |
| `long Generation` | Which game world the answers are about; changes with every new game and load (Dustlife on or off). Keep (Generation, last Seq) and start from Seq 0 when it changes: a new world's news counts from 1 again (version 2) |
| `bool Active` | Dustlife runs the war in this world. A mod with war stories of its own can step aside (as TWR does for KUO) |
| `News(long sinceSeq)` | The war's news after `sinceSeq`, oldest first (the last 300): `Seq`, `Hours`, `Key`, `Text`, `Importance` (0-3), `Faction`, `Other` (faction keys), `Town` (placement id), `Node` (`town:<placement id>`, as Logistics Core and TWR name it; version 2), `Generation`, `Place`, `X`, `Z`. Keep the last `Seq` and ask from it |
| `Places()` | Every place in the war: `Town`, `Node`, `Name`, `Kind` (base, resource, territory, lair), `Holder` (faction key or ""), `Besieged`, `X`, `Z` |
| `Holder(town)` | Who holds a town (node id, placement id or name): a faction key, or "" |
| `SquadTagPrefix`, `IsDustlifeSquad(tag)` | Dustlife's squads in the game carry a `Platoon.ModTag` starting with `dustlife:`. Leave them out of rival pickers and hunter counts |

News keys worth knowing: `took_<motive>` / `hold_<motive>` (a place taken, with its reason: doctrine, counter, retaliation, food, arms,
lifeline, grievance, opportunity, settle, patrol), `took` / `hold` (no reason recorded), `march`, `claim`, `raid`, `siege_end`, `overrun`,
`resurgence`, `town_changed` / `town_emptied` (the game changed a town), `battle`, `rout`.

## Dustlife tells The Wasteland Remembers (0.4.2)

With The Wasteland Remembers installed, Dustlife reports its war to TWR's news board through `WastelandApi` (by reflection: neither mod
depends on the other), so the towns near a fight talk about it. By node (`ReportAtNode("town:<placement id>", ...)`, TWR's API 4), else by
place name (`Report`, API 2); source `dustlife`; at most 6 reports per world step. Setting: "War news for The Wasteland Remembers" (on).

| Dustlife news | TWR kind | Weight |
| --- | --- | --- |
| `took` / `took_<motive>` (taken from another faction), or `hold_*` within 72 game hours of `overrun` by another faction than the old holder (most falls come in these two steps; 0.4.4) | `town-fell`, faction = the taker | 80 base, 65 resource point, 45 territory |
| `siege_end` | `siege-broken` | 55 |
| `march`, `raid` | `war-march`, `war-raid`, with Dustlife's line | as above |

Not sent: `hold_*` (an empty place settled), `town_changed` / `town_emptied` (TWR sees the game's own town changes itself), and news with
no place. TWR takes the same kind at the same place once in 12 game hours, whoever reports it. Overruns still waiting for their settling are kept in the save (0.4.5), so a fall that straddles a save and load is reported once, as in uninterrupted play.

## War bands scatter The Wasteland Remembers' outlaw camps (0.6.0, a test)

The interim bridge agreed on Meitou-Modding-Ecosystem/RivalryCore#59 (decision 17, card #126), before War Orchestration exists. Setting
"War bands strike outlaw camps" (**off** by default; TWR's side reads `DustlifeApi.Places()` / `Holder()` / `News()` behind its own test
setting). Needs TWR's API 4 (`Outlaws()`, `OutlawAt`, `HideoutStruck`), by reflection.

Once a game hour, a Dustlife war band (patrol or invader) that stands within 1.5 km of a named outlaw's ground (`OutlawAt`) and has a place
of its own faction within 8 km (the outlaw is on its ground: the reason for the strike) calls `HideoutStruck(name, faction, bloodied,
"pza.dustlife")`: a patrol is not bloodied, a war band is. At most one strike per hour. TWR's limits, as its author set them on #59:

- never an outlaw the squad hasn't heard of (`Known`);
- never in the first 7 game days after the outlaw rose or a successor took over: TWR API 5's `StrikableAfter` when it has it; before that,
  7 days from when Dustlife first saw that outlaw run by that boss;
- at most once per outlaw per 30 game days; never a wounded, dead, jailed or player's outlaw;
- `HideoutStruck` answering false is "not now": Dustlife asks again a day later at the earliest.

A strike only scatters (TWR's `HideoutStruck`: the outlaw lies low, or licks its wounds if bloodied; no reprisal). Death, jail and
succession stay TWR's and the player's. The strike times and first sightings are saved after the TWR section, so a reload never hands out a
strike early. The daily log line counts the strikes, TWR's "not now" answers, and why a band on an outlaw's ground held off.

## Dustlife under Rivalry Core's limits (0.5.0)

Rivalry Core (`rivalry.core`) is an optional dependency (`>=0.6.0 <0.7.0`), read by reflection through `ModContext.Dependency<object>`, so
Dustlife builds and runs without it. With it loaded, a war band of a faction hostile to the player becomes the game's squad near the player
only once Rivalry Core lets it: it is a `HostileCandidate` (source `pza.dustlife.war`, key `band:<id>`, grudge 0, travel 0) in Rivalry
Core's queue of who goes next (0.6.2+; `ClaimParty` on 0.6.0 and 0.6.1), so it waits for the one hostile party slot and the rest between
hunts like TWR's crews and Rivalry Core's own hunters. Its claim is renewed at every sync while the squad is within 7 km of the player's
people and released when it marches away or is gone. A band of a faction not hostile to the player passes by and asks nothing. Setting:
"Rivalry Core's limits" (on). Once a game day the log says what was proposed, what went, what waited and what is held.
