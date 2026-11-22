// Dustlife for Meitou: the fuzz harness. Author: PZA. Licence: GPLv3 or later. Never shipped; it never starts the game.
//   squads.handover  Squads.Sync (the hand-over to the game's squads) on random worlds, players, loads, moves, deaths and refusals, on a game
//                    that can or can't take a squad back, with code-mod API 2 or 3: never more squads than the cap; every squad's band alive
//                    and external, every external band with its squad; a band given back is alive and Dustlife's again, and not handed over
//                    again inside its cool-down; nothing given back where the game can't; on API 2 a loaded squad is never left held and a
//                    stand-in never left loose, on API 3 the hold is never touched; every squad handed over is still in the game, lost or
//                    given back
//   save.read        WorldSave.Read on damaged saves of a small war with every module: refused only with InvalidDataException, and then the
//                    world it was to load into is untouched; an undamaged save always loads
//   twr.read         WastelandLink.Read (the overruns waiting for TWR's town-fell) on damaged sections: only the errors the system catches
//                    (InvalidDataException, EndOfStreamException, IOException); what it keeps is bounded, for places in the world
//   outlaw.strike    OutlawLink.Step (war bands scatter TWR outlaw camps) against a TWR stand-in whose outlaws get known, are wounded,
//                    change boss and say "not now" at random, with clock jumps: never an unknown, wounded or downed outlaw, never inside the
//                    first 7 days of its boss (or before TWR's StrikableAfter), never twice in 30 days
//   outlaw.read      OutlawLink.Read on damaged sections: only InvalidDataException, EndOfStreamException or IOException
//   link.read        LinkSave.Read (both link sections after the war) on links holding an earlier load, with either section damaged: it never
//                    throws; what it reports is what the links hold; a damaged overrun section keeps neither, a damaged outlaw section
//                    keeps the overruns; two good sections keep 2 overruns and 3 times
using System.Numerics;
using Dustlife.Core;
using Dustlife.Game;
using Dustlife.Modules;
using PZA.MeitouUi.Fuzz;

static class Program
{
    static int Main(string[] a) => PzaFuzz.Run("Dustlife for Meitou", a,
    [
        new("squads.handover", 3000, HandOver, fz =>
        {
            foreach (var rule in new[] { "handed", "lost", "back.far", "back.goal", "back.refused", "loose", "held.again", "cooldown.kept" })
                fz.Expect(fz.Counted(rule) > 0, $"rule '{rule}' never fired in {fz.Case} cases (a rule that never fires is a failed test)");
        }),
        new("save.read", 1500, SaveRead, fz => fz.Expect(fz.Counted("refused") > 0 && fz.Counted("loaded") > 0, "save.read never refused or never loaded")),
        new("twr.read", 20000, TwrRead, fz => fz.Expect(fz.Counted("kept") > 0, "twr.read never kept a row")),
        new("outlaw.strike", 2000, OutlawStrike, fz =>
        {
            foreach (var rule in new[] { "struck", "notnow", "unknown", "young", "cooling", "down", "away" })
                fz.Expect(fz.Counted(rule) > 0, $"rule '{rule}' never fired in {fz.Case} cases (a rule that never fires is a failed test)");
        }),
        new("outlaw.read", 20000, OutlawRead),
        new("link.read", 10000, LinkRead, fz =>
        {
            foreach (var rule in new[] { "clean", "twr.damaged", "outlaw.damaged" })
                fz.Expect(fz.Counted(rule) > 0, $"rule '{rule}' never fired in {fz.Case} cases (a rule that never fires is a failed test)");
        }),
    ]);

    // -------------------------------------------------------------------------------------------------------------------- outlaw.strike

    static readonly string[] OutlawRules = ["struck", "notnow", "unknown", "young", "cooling", "down", "away"];

    static long[] OutlawCounts(OutlawLink l) => [l.Struck, l.NotNow, l.Unknown, l.TooYoung, l.Cooling, l.Down, l.NotOwnGround];

    static Vector2 Spot(PzaFuzz fz, float r) => new((float)fz.Range(-r, r), (float)fz.Range(-r, r));

    static void OutlawStrike(PzaFuzz fz)
    {
        TwrStandIn.Reset(fz);
        var w = new World();
        int hn = w.AddFaction(Lore.HolyNation, "Holy Nation", false, false);
        int sk = w.AddFaction(Lore.Shek, "Shek", false, false);
        for (int i = fz.Int(1, 4); i > 0; i--) w.AddPlace("p" + i, "P" + i, Spot(fz, 20000), PlaceKind.Base, 3, fz.Bool() ? hn : sk);
        w.AddModule(new Warfare(new WarfareConfig { SeedGarrisons = false }));
        w.StartNew();
        for (int i = fz.Int(1, 5); i > 0; i--) w.SpawnBand(fz.Bool() ? hn : sk, 0, fz.Bool() ? BandRole.Patrol : BandRole.Invader, Spot(fz, 20000), "warfare");
        var link = new OutlawLink(() => typeof(TwrStandIn));
        var bossSince = new Dictionary<string, double>(StringComparer.Ordinal);
        var lastStrike = new Dictionary<string, double>(StringComparer.Ordinal);
        double now = fz.Range(0, 1000);
        for (int t = fz.Int(1, 400); t > 0; t--)
        {
            now += fz.Below(10) == 0 ? fz.Range(0, 24 * 40) : fz.Range(0, 3);
            TwrStandIn.Shuffle(fz, now, bossSince);
            foreach (var b in w.Bands) if (fz.Chance(0.2)) b.Pos = fz.Pick(TwrStandIn.Camps.Values.ToList()) + Spot(fz, 2000);
            long[] before = OutlawCounts(link);
            int calls = TwrStandIn.Calls.Count;
            link.Step(w, now);
            long[] after = OutlawCounts(link);
            for (int i = 0; i < OutlawRules.Length; i++) fz.Count(OutlawRules[i], after[i] - before[i]);
            foreach (var (name, took) in TwrStandIn.Calls.Skip(calls))
            {
                var row = TwrStandIn.Rows[name];
                fz.Expect(row["Known"] is true, $"{name} struck before the squad heard of them");
                fz.Expect(row["Status"] is "alive" or "succeeded" && row["Mood"] is not "wounded", $"{name} struck while {row["Status"]} / {row["Mood"]}");
                if (row.TryGetValue("StrikableAfter", out var sa) && sa is double grace)
                    fz.Expect(now >= grace, $"{name} struck at {now:0.#} before TWR's StrikableAfter {grace:0.#}");
                else
                    fz.Expect(now - bossSince[name] >= OutlawLink.GraceHours, $"{name} struck {now - bossSince[name]:0.#} h after its boss took over (a week: {OutlawLink.GraceHours})");
                if (!took) continue;
                fz.Expect(!lastStrike.TryGetValue(name, out double last) || now - last >= OutlawLink.CooldownHours, $"{name} struck twice in {now - last:0.#} h");
                lastStrike[name] = now;
            }
        }
    }

    /// <summary>A stand-in for TWR's outlaw surface (WastelandApi 4, and 5's StrikableAfter at random) whose outlaws the fuzz changes.</summary>
    static class TwrStandIn
    {
        public const int Version = 4;
        public static readonly Dictionary<string, Dictionary<string, object>> Rows = new(StringComparer.Ordinal);
        public static readonly Dictionary<string, Vector2> Camps = new(StringComparer.Ordinal);
        public static readonly List<(string Name, bool Took)> Calls = [];
        static PzaFuzz? s_fz;
        static bool s_api5;

        public static void Reset(PzaFuzz fz)
        {
            s_fz = fz;
            s_api5 = fz.Chance(0.3);
            Rows.Clear();
            Camps.Clear();
            Calls.Clear();
            for (int i = fz.Int(1, 4); i > 0; i--)
            {
                string n = "Outlaw " + i;
                Rows[n] = new() { ["Name"] = n, ["Status"] = "alive", ["Successor"] = "", ["Mood"] = "raiding", ["Known"] = fz.Chance(0.7) };
                Camps[n] = Spot(fz, 20000);
            }
        }

        public static void Shuffle(PzaFuzz fz, double now, Dictionary<string, double> bossSince)
        {
            string[] moods = ["raiding", "lying-low", "gathering", "wounded"], ends = ["alive", "dead", "jailed", "joined"];
            foreach (var (n, r) in Rows)
            {
                if (bossSince.TryAdd(n, now) && s_api5) r["StrikableAfter"] = now + OutlawLink.GraceHours;
                if (fz.Chance(0.05)) r["Known"] = true;
                if (fz.Chance(0.05)) r["Mood"] = fz.Pick(moods);
                if (fz.Chance(0.02)) r["Status"] = fz.Pick(ends);
                if (!fz.Chance(0.06)) continue;
                r["Status"] = "succeeded";                                                    // a new boss: a new week
                r["Successor"] = "Heir " + fz.Int(0, 99999);
                bossSince[n] = now;
                if (s_api5) r["StrikableAfter"] = now + OutlawLink.GraceHours;
            }
        }

        public static IReadOnlyList<IReadOnlyDictionary<string, object>> Outlaws() =>
            [.. Rows.Values.Select(r => (IReadOnlyDictionary<string, object>)new Dictionary<string, object>(r))];

        public static string OutlawAt(float x, float z, float within) =>
            Camps.Where(c => Vector2.Distance(c.Value, new Vector2(x, z)) <= within).Select(c => c.Key).FirstOrDefault() ?? "";

        public static bool HideoutStruck(string name, string by, bool bloodied, string source)
        {
            bool took = !s_fz!.Chance(0.2);
            Calls.Add((name, took));
            return took;
        }
    }

    /// <summary>A string length of five 0xFF bytes at <paramref name="at"/> (BinaryReader throws FormatException on it).</summary>
    static byte[] BadLength(byte[] b, int at)
    {
        var c = (byte[])b.Clone();
        for (int i = at; i < Math.Min(at + 5, c.Length); i++) c[i] = 0xFF;
        return c;
    }

    static void OutlawRead(PzaFuzz fz)
    {
        using var ms = new MemoryStream();
        new OutlawLink(() => null).Write(ms);
        using var o = new MemoryStream();
        using (var bw = new BinaryWriter(o))
        {
            bw.Write(ms.ToArray()[..8]);                                                       // magic and version as the link writes them
            for (int part = 0; part < 3; part++)
            {
                bw.Write(2);
                bw.Write("Dolan Pike");
                bw.Write(fz.Range(0, 1000));
                bw.Write("Pyke Ludlow|Esme Ludlow");
                bw.Write(double.NaN);
            }
        }

        byte[] valid = o.ToArray();
        byte[] bytes = fz.Below(4) switch { 0 => valid, 1 => fz.Mutate(valid), 2 => BadLength(valid, 12), _ => valid[..fz.Int(0, valid.Length)] };
        try
        {
            int kept = new OutlawLink(() => null).Read(new MemoryStream(bytes));
            fz.Expect(kept >= 0, $"kept {kept}");
            if (ReferenceEquals(bytes, valid)) fz.Expect(kept == 3, $"the good section kept {kept}, not 3 (the NaN rows dropped)");
        }
        catch (Exception e) when (e is InvalidDataException or EndOfStreamException or IOException) { }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            fz.Expect(false, $"OutlawLink.Read threw {e.GetType().Name}: {e.Message}");
        }
    }

    // ------------------------------------------------------------------------------------------------------------------ squads.handover

    sealed class Host(PzaFuzz fz, bool canGiveBack, bool holdStopsLoaded) : ISquadHost
    {
        public readonly Dictionary<int, (bool Dead, int Size, Vector2 Pos)> Squads = [];
        public readonly HashSet<int> LoadedNow = [], Held = [];
        public double RefuseSpawn, RefuseBack;
        int _next = 1000;

        public int Spawn(int faction, string template, int home, Vector2 at, Vector2 goal, string tag)
        {
            if (fz.Chance(RefuseSpawn)) return -1;
            Squads[_next] = (false, fz.Int(1, 12), at);
            Held.Add(_next);
            return _next++;
        }

        public void Send(int squad, Vector2 goal) { }

        public (bool Dead, int Size, Vector2 Pos)? Look(int squad) => Squads.TryGetValue(squad, out var s) ? s : null;

        public IEnumerable<(int Squad, string Tag)> Tagged() => [];

        public bool CanGiveBack => canGiveBack;

        public bool GiveBack(int squad)
        {
            if (!canGiveBack || fz.Chance(RefuseBack) || !Squads.Remove(squad)) return false;
            LoadedNow.Remove(squad);
            Held.Remove(squad);
            return true;
        }

        public bool Loaded(int squad) => LoadedNow.Contains(squad);

        public bool HoldStopsLoaded => holdStopsLoaded;

        public void SetHeld(int squad, bool held)
        {
            if (held) Held.Add(squad);
            else Held.Remove(squad);
        }
    }

    static Vector2 Point(PzaFuzz fz) => new((float)fz.Range(-120000, 120000), (float)fz.Range(-120000, 120000));   // the game's positions are finite

    static void HandOver(PzaFuzz fz)
    {
        var w = new World();
        int hn = w.AddFaction(Lore.HolyNation, "Holy Nation", false, false);
        int sk = w.AddFaction(Lore.Shek, "Shek", false, false);
        var places = new List<int>();
        for (int i = fz.Int(2, 6); i > 0; i--) places.Add(w.AddPlace("p" + i, "Place " + i, Point(fz), PlaceKind.Base, 3, fz.Bool() ? hn : sk));
        w.AddModule(new Warfare(new WarfareConfig { SeedGarrisons = false }));
        w.StartNew();
        for (int i = fz.Int(1, 10); i > 0; i--)
        {
            var b = w.FindBand(w.SpawnBand(fz.Bool() ? hn : sk, fz.Int(1, 3), fz.Bool() ? BandRole.Invader : BandRole.Patrol, Point(fz), "warfare"))!;
            w.Send(b, fz.Pick(places), b.Role);
        }

        var cfg = Lore.Squads();
        if (fz.Chance(0.3)) cfg.MaxSquads = fz.Int(0, 6);
        if (fz.Chance(0.3)) cfg.FarSyncs = fz.Int(1, 6);
        if (fz.Chance(0.3)) cfg.CooldownSyncs = fz.Int(0, 40);
        var squads = new Squads(cfg);
        var host = new Host(fz, fz.Chance(0.7), fz.Bool()) { RefuseSpawn = fz.Chance(0.3) ? 0.3 : 0, RefuseBack = fz.Chance(0.3) ? 0.4 : 0 };
        var backAt = new Dictionary<int, long>();
        var players = new List<Vector2> { Point(fz) };

        for (int t = fz.Int(1, 120); t > 0; t--)
        {
            // the world between syncs: the player moves, squads load, unload, walk, reach their goal or die
            if (fz.Chance(0.2)) players = fz.Below(6) == 0 ? [] : [.. Enumerable.Range(0, fz.Int(1, 3)).Select(_ => Point(fz))];
            foreach (int id in host.Squads.Keys.ToList())
            {
                var s = host.Squads[id];
                if (fz.Chance(0.15)) { if (!host.LoadedNow.Add(id)) host.LoadedNow.Remove(id); }
                if (fz.Chance(0.2)) s.Pos = fz.Chance(0.4) && squads.SquadOf.FirstOrDefault(e => e.Value == id) is { Value: > 0 } e && w.FindBand(e.Key) is { } bb
                    ? bb.TargetPos + new Vector2((float)fz.Range(-400, 400), (float)fz.Range(-400, 400))
                    : Point(fz);
                if (fz.Chance(0.02)) s = s with { Dead = true };
                if (fz.Chance(0.02)) s = s with { Size = 0 };
                host.Squads[id] = s;
                if (fz.Chance(0.01)) host.Squads.Remove(id);
            }

            long handed = squads.HandedOver, lost = squads.LostWithSquad, far = squads.GivenBackFar, goal = squads.GivenBackAtGoal,
                refused = squads.GiveBackRefused, loose = squads.ReleasedLoaded, again = squads.HeldAgain;
            var before = squads.SquadOf.Keys.ToHashSet();
            squads.Sync(w, host, players);

            fz.Count("handed", squads.HandedOver - handed);
            fz.Count("lost", squads.LostWithSquad - lost);
            fz.Count("back.far", squads.GivenBackFar - far);
            fz.Count("back.goal", squads.GivenBackAtGoal - goal);
            fz.Count("back.refused", squads.GiveBackRefused - refused);
            fz.Count("loose", squads.ReleasedLoaded - loose);
            fz.Count("held.again", squads.HeldAgain - again);

            fz.Expect(squads.SquadOf.Count <= Math.Max(cfg.MaxSquads, 0), $"{squads.SquadOf.Count} squads over the cap of {cfg.MaxSquads}");
            foreach (var (bandId, squad) in squads.SquadOf)
            {
                var b = w.FindBand(bandId);
                fz.Expect(b is { Alive: true, External: true }, $"squad {squad}'s band {bandId} is {(b == null ? "gone" : $"alive {b.Alive}, external {b.External}")}");
                if (host.HoldStopsLoaded)
                {
                    if (!before.Contains(bandId)) continue;                      // handed over this sync: not yet followed
                    bool loaded = host.LoadedNow.Contains(squad), held = host.Held.Contains(squad);
                    fz.Expect(loaded != held, $"API 2: squad {squad} {(loaded ? "loaded and still held" : "a stand-in and left loose")}");
                }
                else fz.Expect(host.Held.Contains(squad), $"API 3: squad {squad}'s hold was taken off");
            }

            foreach (var b in w.Bands.Where(x => x.Alive && x.External))
                fz.Expect(squads.SquadOf.ContainsKey(b.Id), $"external band {b.Id} has no squad");
            foreach (int bandId in before.Where(id => !squads.SquadOf.ContainsKey(id)))
                if (w.FindBand(bandId) is { Alive: true } b)
                {
                    fz.Expect(!b.External, $"band {bandId} left the game but is still external");
                    backAt[bandId] = squads.Syncs;
                }

            foreach (var (bandId, at) in backAt)
            {
                if (!squads.SquadOf.ContainsKey(bandId) || before.Contains(bandId)) continue;
                fz.Expect(squads.Syncs - at > cfg.CooldownSyncs, $"band {bandId} handed over again {squads.Syncs - at} syncs after it was given back (cool-down {cfg.CooldownSyncs})");
            }
            foreach (var (bandId, at) in backAt)
                if (!squads.SquadOf.ContainsKey(bandId) && squads.Syncs - at <= cfg.CooldownSyncs && cfg.CooldownSyncs > 0) fz.Count("cooldown.kept");

            if (!host.CanGiveBack) fz.Expect(squads.GivenBackFar + squads.GivenBackAtGoal == 0, "a squad given back where the game can't take one");
            if (!host.HoldStopsLoaded) fz.Expect(squads.ReleasedLoaded == 0, "API 3: a hold released");
            fz.Expect(squads.HandedOver == squads.SquadOf.Count + squads.LostWithSquad + squads.GivenBackFar + squads.GivenBackAtGoal,
                $"handed over {squads.HandedOver} != in the game {squads.SquadOf.Count} + lost {squads.LostWithSquad} + given back {squads.GivenBackFar + squads.GivenBackAtGoal}");
        }
    }

    // ------------------------------------------------------------------------------------------------------------------------ save.read

    sealed class Relations(params (int A, int B)[] hostile) : IRelations
    {
        public bool Hostile(int x, int y) => hostile.Any(h => (h.A == x && h.B == y) || (h.A == y && h.B == x));
        public bool HostileToPlayer(int faction) => false;
    }

    static readonly Relations Rel = new((0, 1), (0, 2), (1, 2), (3, 0), (3, 1), (4, 0), (4, 1), (4, 2));

    static World Build(ulong seed)
    {
        var w = new World();
        w.Rng.State = seed;
        w.AddFaction("hn", "Holy Nation", false, false);
        w.AddFaction("sk", "Shek", false, false);
        w.AddFaction("st", "Slave Traders", false, true);
        w.AddFaction("db", "Dust Bandits", false, false);
        w.AddFaction("beak", "Beak Things", true, false);
        (string Key, float X, float Z, PlaceKind Kind, int Res, int Owner)[] places =
        [
            ("stack", 0, 0, PlaceKind.Base, 3, 0), ("farm", 20000, 5000, PlaceKind.Resource, 2, 0), ("squin", 60000, 0, PlaceKind.Base, 3, 1),
            ("ruins", 40000, 15000, PlaceKind.Territory, 2, 1), ("slave", 30000, -30000, PlaceKind.Resource, 2, 2),
            ("camp", 30000, 30000, PlaceKind.Territory, 1, 3), ("nest", 10000, 40000, PlaceKind.Lair, 0, 4), ("tower", 50000, -15000, PlaceKind.Territory, 1, -1),
        ];
        foreach (var p in places) w.AddPlace(p.Key, p.Key, new Vector2(p.X, p.Z), p.Kind, p.Res, p.Owner);
        w.Roads = new Roads();
        for (int i = 0; i < w.Places.Count; i++)
            for (int k = i + 1; k < w.Places.Count; k++)
                if (Vector2.Distance(w.Places[i].Pos, w.Places[k].Pos) < 40000)
                    w.Roads.Add(i, k, Vector2.Distance(w.Places[i].Pos, w.Places[k].Pos) * 1.2f);
        w.AddModule(new Economy(new EconomyConfig { CutRoads = true, Profiles = [new EconomyProfile { Faction = "st", PoolOf = "sk" }] }));
        w.AddModule(new Slavery());
        w.AddModule(new Fauna());
        w.AddModule(new Warfare(new WarfareConfig { UseEconomy = true, ClaimsHold = true, MusterPatienceHours = 24, Motives = new MotiveConfig() }));
        return w;
    }

    static byte[] SaveOf(World w)
    {
        using var ms = new MemoryStream();
        WorldSave.Write(w, ms);
        return ms.ToArray();
    }

    static string State(World w) =>
        string.Join('\n', new[] { $"{w.Now} {w.Rng.State} {w.NameRng.State}" }
            .Concat(w.Places.Select(p => $"{p.Key}:{p.Owner}"))
            .Concat(w.Bands.Select(b => $"{b.Id} {b.Faction} {b.Size} {b.State} {b.Pos.X} {b.Pos.Y}")));

    static readonly List<byte[]> Saves = [];

    static void SaveRead(PzaFuzz fz)
    {
        if (Saves.Count < 3)
        {
            var w = Build((ulong)(Saves.Count + 1));
            w.StartNew();
            for (int h = 1; h <= (Saves.Count + 1) * 6 * 24; h++) w.Advance(h, Rel);
            Saves.Add(SaveOf(w));
        }

        byte[] good = fz.Pick(Saves);
        bool damage = !fz.Chance(0.05);
        byte[] bytes = !damage ? good : fz.Below(3) == 0 ? good[..fz.Int(0, good.Length - 1)] : fz.Mutate(good);   // a third cut off short
        var target = Build(99);
        string before = State(target);
        try
        {
            WorldSave.Read(target, new MemoryStream(bytes));
            fz.Count("loaded");
        }
        catch (InvalidDataException)
        {
            fz.Count("refused");
            fz.Expect(damage, "an undamaged save was refused");
            fz.Expect(State(target) == before, "a refused save changed the world");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            fz.Expect(false, $"WorldSave.Read threw {e.GetType().Name}: {e.Message}");
        }
    }

    // ------------------------------------------------------------------------------------------------------------------------- twr.read

    static World? s_twrWorld;
    static byte[]? s_twrGood;

    static byte[]? s_outlawGood;

    /// <summary>A good outlaw section: the link's header, then one finite time in each of its three parts.</summary>
    static byte[] OutlawGood()
    {
        if (s_outlawGood != null) return s_outlawGood;
        using var ms = new MemoryStream();
        new OutlawLink(() => null).Write(ms);
        using var o = new MemoryStream();
        using (var bw = new BinaryWriter(o))
        {
            bw.Write(ms.ToArray()[..8]);
            for (int part = 0; part < 3; part++)
            {
                bw.Write(1);
                bw.Write("Dolan Pike");
                bw.Write(500.0);
            }
        }

        return s_outlawGood = o.ToArray();
    }

    static void LinkRead(PzaFuzz fz)
    {
        TwrRead(fz);                                                     // builds s_twrWorld and s_twrGood (and fuzzes the overrun reader once more)
        var w = s_twrWorld!;
        byte[] twrGood = s_twrGood!, outlawGood = OutlawGood();
        byte[] Damage(byte[] b) => fz.Below(3) switch { 0 => fz.Mutate(b), 1 => b[..fz.Int(0, b.Length)], _ => BadLength(b, 8 + fz.Int(0, 4)) };

        int which = fz.Below(3);                                         // 0: both good, 1: the overrun section damaged, 2: the outlaw section damaged
        byte[] twr = which == 1 ? Damage(twrGood) : twrGood, outlaw = which == 2 ? Damage(outlawGood) : outlawGood;
        bool twrIntact = twr.AsSpan().SequenceEqual(twrGood), outlawIntact = outlaw.AsSpan().SequenceEqual(outlawGood);

        var (twrLink, outlawLink) = (new WastelandLink(() => null), new OutlawLink(() => null));
        LinkSave.Read(new MemoryStream([.. twrGood, .. outlawGood]), w, twrLink, outlawLink);  // the links hold an earlier load
        try
        {
            var r = LinkSave.Read(new MemoryStream([.. twr, .. outlaw]), w, twrLink, outlawLink);
            fz.Expect(r.Overruns == twrLink.Pending, $"reported {r.Overruns} overruns, the link holds {twrLink.Pending}");
            fz.Expect(r.OutlawTimes == outlawLink.Times, $"reported {r.OutlawTimes} outlaw times, the link holds {outlawLink.Times}");
            if (r.Damage?.StartsWith("the save's waiting overruns", StringComparison.Ordinal) == true)
                fz.Expect(twrLink.Pending == 0 && outlawLink.Times == 0, $"a damaged overrun section kept {twrLink.Pending} overruns and {outlawLink.Times} times");
            if (twrIntact && outlawIntact) fz.Expect(r == new LinkSave.Result(2, 3, null), $"two good sections gave {r}");
            if (twrIntact && !outlawIntact && r.Damage != null)
                fz.Expect(r.Overruns == 2 && twrLink.Pending == 2, $"a damaged outlaw section lost the overruns ({r.Overruns}, holding {twrLink.Pending})");
            fz.Count(which == 0 ? "clean" : which == 1 ? "twr.damaged" : "outlaw.damaged");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            fz.Expect(false, $"LinkSave.Read threw {e.GetType().Name}: {e.Message} (the load would turn Dustlife off for the world)");
        }
    }

    static void TwrRead(PzaFuzz fz)
    {
        var w = s_twrWorld ??= Build(5);
        if (s_twrGood == null)
        {
            using var ms = new MemoryStream();
            new WastelandLink(() => null).Write(ms, w);
            byte[] head = ms.ToArray()[..8];                          // magic and version as the link writes them
            using var o = new MemoryStream();
            using (var bw = new BinaryWriter(o))
            {
                bw.Write(head);
                bw.Write(3);
                foreach (var (p, f, hrs) in new[] { ("stack", "sk", 10.0), ("ruins", "", 2.5), ("gone", "hn", 1.0) }) { bw.Write(p); bw.Write(f); bw.Write(hrs); }
            }
            s_twrGood = o.ToArray();
        }

        byte[] bytes = fz.Below(5) switch { 0 => s_twrGood, 1 => fz.Mutate(s_twrGood), 2 => s_twrGood[..fz.Int(0, s_twrGood.Length)], 3 => BadLength(s_twrGood, 12), _ => fz.Mutate(new byte[fz.Int(0, 64)]) };
        var link = new WastelandLink(() => null);
        try
        {
            int kept = link.Read(new MemoryStream(bytes), w);
            fz.Expect(kept >= 0 && kept <= w.Places.Count && kept == link.Pending, $"kept {kept}, pending {link.Pending}");
            fz.Count("kept", kept);
            if (ReferenceEquals(bytes, s_twrGood)) fz.Expect(kept == 2, $"the good section kept {kept}, not 2");
        }
        catch (Exception e) when (e is InvalidDataException or EndOfStreamException or IOException) { }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            fz.Expect(false, $"WastelandLink.Read threw {e.GetType().Name}: {e.Message} (DustlifeSystem.ReadLink doesn't catch it)");
        }
    }
}
