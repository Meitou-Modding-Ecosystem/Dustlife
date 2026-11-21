# Dustlife for Meitou

> **Version 0.4.2** (the war runs in the game, off screen, from the game's own towns and roads; marching bands near the player become the game's own squads; its news reaches The Wasteland Remembers' news board; not released, not yet tried in game) · Meitou mod id `pza.dustlife` · engine pin `1cc76a0`

Dustlife, A-Life for Kenshi, as a [MeitouClient](https://github.com/Meitou-Modding-Ecosystem/MeitouClient) code mod: the whole map stays at
war while the player is elsewhere. Factions hold towns, raise garrisons from what their towns produce, send their surplus to take more, and
lose what they can't feed or defend. Author: PZA. GPL-3.0-or-later.

- Design: [docs/dustlife.md](https://github.com/Meitou-Modding-Ecosystem/RivalryCore/blob/beta/docs/dustlife.md) and the tracking issue
  Meitou-Modding-Ecosystem/RivalryCore#29. The Bast Front: RivalryCore#31.
- It builds on Logistics Core's nodes and edges (Meitou-Modding-Ecosystem/LogisticsCore): every placed town is a node, the game's roads are
  the edges.
- Build and run locally: [docs/play.md](docs/play.md). Setting up the repos: [docs/local-development.md](docs/local-development.md).

## Port order

One step at a time, each with its tests:

1. **The skeleton** (this version): `meitou.json`, the entry, one empty world system.
2. The core world (places, factions, bands, time, combat) and the garrisons and resources, on synthetic-world tests.
   - **2a, done:** the core world (`Dustlife/Core`): places, factions, bands and leaders, time and catch-up, movement, combat with morale
     breaks, battle scars and the war host's warlord. The random numbers are the reference's bit for bit; every sentence uses the
     reference's keys, so its translations fit. 11 tests; planting six known bugs fails a test each time.
   - **2b, done:** Warfare's foundation (`Dustlife/Modules/Warfare.cs`): garrisons seeded by kind, each faction's band budget from what
     it holds (shrinking evenly at the world's cap), bases raising garrisons back, sieges from the edge, a real town's home guard, and
     who holds each place. 8 more tests; planting seven known bugs fails a test each time.
   - **2c, done:** targets, invasions with mustering and the boldest captains first, patrols, comebacks after losing every base, and the
     lore doctrines (`Dustlife/Modules/Doctrine.cs`): never / only / enemies / order, counter-strikes, hold-and-defend, weak and odds
     gates, war pacing, home keep, skill, and border wars on several fronts with raids. One change from the reference
     (RivalryCore#33): ease-off now stops only the main war; border wars go on. 34 tests; 13 planted doctrine and invasion bugs each
     fail one.
3. **Done:** the economy, animals and slavery (`Dustlife/Modules`), and the balance runner (`Dustlife.Balance`). The runner plays a map
   file (made from the game's data, kept outside the repo) for 90 game days in about 3 seconds:
   `dotnet run --project Dustlife.Balance -c Release -- --map <map.tsv> --runs 100 [--scenario stack|ease]` (`DL_FALSIFIERS=1` prints the
   event behind a broken lore check). Against the reference on the same map, 100 seeds per set-up (C# / C++):

   | | Control | Stack | Ease |
   | --- | --- | --- | --- |
   | UC places on the Bast front at day 90 (of 14) | 10.7 / 10.7 | 10.3 / 10.0 | 9.7 / 9.3 |
   | Bast still moving after day 60 | 96 / 96 | 89 / 94 | 91 / 93 |
   | Stack fell | 10 / 15 | 55 / 43 | 0 / 1 |
   | Lore checks broken | 0 / 0 | 0 / 0 | 1 / 0 |

   The one broken check is the reference's known timing race (1 in 300 there too). Open: Stack falls more often in the stack set-up
   (about 1.7 standard deviations). 40 tests.
   - **3b, done: motives** (`Dustlife/Modules/Motive.cs`, off unless `WarfareConfig.Motives` is set). Every target has a reason and
     every taking says it in the news: the creed's order or counter-strike, hunger (farms when short of food, bases when short of arms),
     grievance (lost in the last three weeks, or held from the start), retaliation, a thinly held neighbour, empty land to work, or a
     patrol that stayed. By default a place with no reason is not a target. Supply roads come with step 4. Balance runner `--motives`,
     100 seeds, control set-up (without / with): UC Bast places at day 90 10.7 / 10.8, still moving after day 60 96 / 95, Stack fell
     10 / 10, lore checks broken 0 / 0. Takings by motive: grievance 55%, opportunity 30%, food 7%, settling 7%, creed under 1% (only
     Stack and Okran's Shield), **no reason 0%** of 2418. 44 tests; three planted motive bugs each fail one.
4. **Done: Logistics Core's graph** (`Dustlife/Core/Roads.cs`). With roads, targets and patrols go by road distance; with cut roads
   (`EconomyConfig.CutRoads`) a place its bloc can't reach from a base by roads its enemies don't block (an enemy town, a siege, or a band
   camped on the road) produces nothing; a faction with no base has no supply line to cut. The town that cuts a road home is a target
   for the new **lifeline** motive. Balance runner `--edges <file>` (Logistics Core's 618 base-game edges, 563 matched to the map file's
   places; the 9 places the old map lacks hold the rest) and `--cutroads`. 100 seeds, motives on (straight / roads / roads + cut roads):
   UC Bast places at day 90 10.8 / 10.6 / 10.6, still moving after day 60 95 / 97 / 97, Stack fell 10 / 11 / 10, lore checks broken 0 / 0 / 0.
   Cut roads cost 275 place-days of output a run, mostly small factions'. **Lifeline never fires in the lore war on the real map**:
   sampled every 15 days, the Holy Nation, the UC and the Shek never had a place cut off (each place has about six roads, so there is a
   way round); the rule is tested on its own. 6 tests; six planted road bugs each fail one.
5. Bands as real platoons through the world hooks.
   - **5a, done: the war in the game** (`Dustlife/Game`, `DustlifeSystem.cs`). The world is built from the game's placed towns and
     factions (`GameMap`, the reference bridge's rules; the balance runner builds through the same code and gives identical results),
     Logistics Core's edges (`Game/Vendor/VanillaEdges.cs`, a copy of LogisticsCore c12ced5 until it ships as a Meitou dependency) and the
     lore set-up (`Game/Lore.cs`, shared with the runner). It runs on the game's clock and relations, writes its bigger news to the log,
     and has four settings (on, motives, cut roads, news). **The save** (`Core/Save.cs`): places and factions by key, so a save loads into
     a changed world; a damaged save is refused before anything changes, and the war starts afresh. A loaded world goes on exactly as
     the one that never stopped (tested on three seeds over 20 days, byte for byte).
   - **5b, done: town changes** (`Game/TownWatch.cs`). When the game changes a town (an override version: Esata dies and Stack becomes
     the Shek's), the place passes to its new holder, the old garrison leaves for its nearest place (or breaks up), and the news says so.
     It reads the towns, so it works on 0.20 (changes at zone load) and on engines with off-screen overrides (at once).
   - Known limits: the base map places only nest markers; the game makes its real nests (camps, animal nests) at run time, out of a
     mod's reach on 0.20, so there are no animal lairs yet. Settings take effect at the next new game or load.
   - **5c, done: real squads** (`Game/Squads.cs`, `Game/EngineSquads.cs`). A marching invader or patrol of the Holy Nation, the UC or the
     Shek, 2.5 to 7 km from one of the player's people (out of view, near enough to meet), becomes the game's squad of its faction's war
     template (`Lore.Squads`: Holy Army force / Patrol, Empire army / small patrol, Shek base attack medium / Shek Patrol; chosen by name,
     because a faction's own squad list holds its civilians), tagged `dustlife:<band>`, held and sent to the band's goal. The band is then
     external: the core neither moves it nor fights it off screen; its position and losses come from the squad; when the squad is close to
     its goal the band arrives as any band does; a beaten squad ends the band. At most four at once, one more per sync. After a load the
     squads are found again by their tags (the engine gives platoons new ids), and an external band whose squad the save no longer has
     was beaten: it is never re-placed, so a killed crew does not come back. The engine has no despawn, so a squad stays the game's for
     good; that is why only marching bands near the player are handed over. Setting "Real squads near you". 4 tests with a fake game;
     four planted bugs each fail one.
   - Not yet: beaten garrisons rehomed as guests (`RehomePlatoon`, newer engines only), raid parties, the Hub theatre, rebuilt ruins
     through world states, and an API for other mods (TheWastelandRemembers' planned incoming events; its rival picker can skip squads
     tagged `dustlife:`).
6. The economy rules, one at a time, each with its falsifier.

## Layout

- `engine/`: MeitouClient as a git submodule, pinned to the engine commit the mod builds against. After a pull that moves it:
  `git submodule update --init`.
- `Dustlife/`: the mod's project and `meitou.json`. `Dustlife.Tests/`: its tests (synthetic worlds, no game install needed).
- `meitou.lock.json`: the framework releases the mods build against
  ([framework lock](https://github.com/TheIvoryDisaster/GutenStructure/blob/beta/docs/framework-lock.md)); the Framework update workflow moves it
  to new releases and merges when CI passes ([framework update](https://github.com/TheIvoryDisaster/GutenStructure/blob/beta/docs/framework-update.md)).
- `Play/`: one-click build, deploy and run.
- `tools/`: `refcheck.sh` and `RefCheck/`, the engine-reference check ([docs/refcheck.md](docs/refcheck.md)).
- `Directory.Build.props` imports the engine's, so mods build with the game's settings (warnings are errors).

## Branches

PRs into `beta`; CI (build and tests) must pass. `beta` is promoted to `main` once tested in play, and production goes through a PR to
`release`. A tag `<slug>-v<version>` on `release` publishes the mod's zip (`.github/workflows/release.yml`).
