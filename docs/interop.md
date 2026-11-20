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
