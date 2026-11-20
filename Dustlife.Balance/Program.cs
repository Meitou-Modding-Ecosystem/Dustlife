// Dustlife (A-Life for Kenshi) for Meitou. Author: PZA. Licence: GPLv3 or later.
//
// The balance runner (port step 3): the reference's balance.cpp in C#. It builds the world from a map file the way the reference did
// (same place kinds, resource points, caps, locked places, modules, speed), runs it hour by hour with no player near anything, and
// reports what the reference's runs recorded, so the two can be compared on the same seeds.
//   Usage: dotnet run -- --map <map.tsv> [--days 90] [--seed 1] [--runs 1] [--scenario stack|ease] [--tag NAME] [--out runs.csv] [--motives]
//          [--edges <edges.tsv>] [--cutroads] [--hub]
// The map file (F / H / P / N lines: factions, hostile pairs, factions hostile to the player, places) is made from the game's data and
// stays outside the repo.
using System.Globalization;
using System.Numerics;
using Dustlife.Core;
using Dustlife.Game;
using Dustlife.Modules;

const string kHn = Lore.HolyNation, kUc = Lore.UnitedCities, kShek = Lore.Shek, kSt = Lore.SlaveTraders, kTg = Lore.TradersGuild;
var bastCenter = Lore.BastCenter;
const float bastRadius = Lore.BastRadius, ucWeakBelow = Lore.UcWeakBelow;

string mapPath = "", scenario = "", tag = "", outCsv = "dustlife_balance_runs.csv";
int days = 90, runs = 1;
bool motives = false, cutRoads = false, hub = false;
string edgesPath = "";
ulong seed = 1;
for (int i = 0; i < args.Length; i++)
{
    string a = args[i];
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} needs a value");
    switch (a)
    {
        case "--map": mapPath = Next(); break;
        case "--days": days = Math.Clamp(int.Parse(Next(), CultureInfo.InvariantCulture), 1, 3650); break;
        case "--seed": seed = ulong.Parse(Next(), CultureInfo.InvariantCulture); break;
        case "--runs": runs = Math.Clamp(int.Parse(Next(), CultureInfo.InvariantCulture), 1, 1000); break;
        case "--scenario": scenario = Next(); if (scenario is not ("stack" or "ease")) throw new ArgumentException("scenario: stack or ease"); break;
        case "--tag": tag = Next(); break;
        case "--out": outCsv = Next(); break;
        case "--motives": motives = true; break;
        case "--edges": edgesPath = Next(); break;
        case "--cutroads": cutRoads = true; break;
        case "--hub": hub = true; break;
        default: throw new ArgumentException($"unknown argument {a}");
    }
}

var map = MapFile.Read(mapPath);
if (cutRoads && edgesPath.Length == 0) throw new ArgumentException("--cutroads needs --edges");
var results = new List<RunResult>();
(int Matched, int Missed) roadsMatched = (0, 0);
for (int k = 0; k < runs; k++) results.Add(Run(seed + (ulong)k));

bool fresh = !File.Exists(outCsv);
using (var csv = new StreamWriter(outCsv, append: true))
{
    if (fresh) csv.WriteLine("tag,scenario,seed,days,ucBast30,ucBast60,ucBast90,bastTotal,hnBastTook,ucBastTook,bastLastDay,osPowLate,ucOsTook,stackFell,falsifiers,hnNoBase,skNoBase,ms");
    foreach (var r in results)
        csv.WriteLine(string.Join(",", tag, scenario.Length == 0 ? "control" : scenario, r.Seed, days, r.UcBast[0], r.UcBast[1], r.UcBast[2], r.BastTotal,
            r.HnBastTook, r.UcBastTook, r.BastLastDay, r.OsPowLate.ToString("0.0", CultureInfo.InvariantCulture), r.UcOsTook, r.StackFell, r.Falsifiers,
            r.NoBaseDay[0], r.NoBaseDay[2], r.Ms));
}

double Mean(Func<RunResult, double> f) => results.Average(f);
Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
    $"{tag} / {(scenario.Length == 0 ? "control" : scenario)} / {days} days: {runs} runs, {Mean(r => r.Ms):0} ms a run"));
Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
    $"  Bast front: UC holds {Mean(r => r.UcBast[0]):0.0} / {Mean(r => r.UcBast[1]):0.0} / {Mean(r => r.UcBast[2]):0.0} of {results[0].BastTotal} (days 30/60/90) | Holy Nation took {Mean(r => r.HnBastTook):0.0}, UC took {Mean(r => r.UcBastTook):0.0} per run"));
Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
    $"  front still moving after day 60 in {results.Count(r => r.BastLastDay > 60)} of {runs} runs"));
Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
    $"  Okran's Shield: Holy Nation power (last 30 days) {Mean(r => r.OsPowLate):0} | UC took it in {results.Count(r => r.UcOsTook > 0)} runs | Stack fell in {results.Count(r => r.StackFell > 0)} runs"));
Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
    $"  Holy Nation lost every base in {results.Count(r => r.NoBaseDay[0] > 0)}, Shek in {results.Count(r => r.NoBaseDay[2] > 0)} | falsifiers {results.Sum(r => r.Falsifiers)}"));
Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
    $"  the Hub{(hub ? " (theatre on)" : "")}: Holy Nation took {Mean(r => r.HnHubTook):0.0}, Shek {Mean(r => r.SkHubTook):0.0} per run | moving after day 60 in {results.Count(r => r.HubLastDay > 60)} of {runs} | raid parties {Mean(r => r.RaidParties):0.0} a run"));
if (edgesPath.Length > 0)
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"  roads: {roadsMatched.Matched} edges on the map ({roadsMatched.Missed} not matched){(cutRoads ? $" | cut roads cost {Mean(r => r.CutHours) / 24:0} place-days of output a run, cutters found {Mean(r => r.Cutters):0}, roads closed by camps {Mean(r => r.BandBlocks):0}" : "")}"));
if (motives)
{
    // the motive falsifier: a taking with no reason is a failure of the rules, so their share should be small
    var names = Enum.GetNames<Motive>();
    long all = results.Sum(r => r.TookFor.Sum());
    string Share(int m) => all == 0 ? "0" : (100.0 * results.Sum(r => r.TookFor[m]) / all).ToString("0", CultureInfo.InvariantCulture);
    Console.WriteLine("  targets chosen by motive (per run): " + string.Join(", ", names.Select((n, m) => $"{n} {results.Average(r => r.PickedFor[m]):0}")));
    Console.WriteLine("  takings by motive (%): " + string.Join(", ", names.Select((n, m) => $"{n} {Share(m)}")) + $" of {all}");
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  expansion targets passed over for no motive: {Mean(r => r.NoMotive):0} a run"));
}

return;

RunResult Run(ulong s)
{
    var w = new World();
    w.Rng.State = s == 0 ? 1 : unchecked(s * 0x9E3779B97F4A7C15UL);
    w.Cfg.Speed = Lore.GameSpeed;
    var rel = map.Build(w);
    if (edgesPath.Length > 0) roadsMatched = RoadFile.Load(edgesPath, w);
    var war = Lore.War(motives, hub);
    var economy = new Economy(Lore.Economy(cutRoads));
    w.AddModule(economy);                     // before Warfare: income counts before it spends
    w.AddModule(new Slavery());
    w.AddModule(new Fauna());
    var warfare = new Warfare(war);
    w.AddModule(warfare);
    w.StartNew();
    Scenario(w);

    var r = new RunResult { Seed = s };
    int fHn = w.FindFaction(kHn), fUc = w.FindFaction(kUc), fSk = w.FindFaction(kShek), fSt = w.FindFaction(kSt), fTg = w.FindFaction(kTg);
    int[] owner0 = [.. w.Places.Select(p => p.Owner)];
    int[] lastHeld = [.. owner0];
    int os = w.Places.FindIndex(p => p.Name == "Okran's Shield");
    long lastSeq = 0;
    double clock = 0;
    var sw = System.Diagnostics.Stopwatch.StartNew();
    for (int day = 1; day <= days; day++)
    {
        for (int h = 0; h < 24; h++)
        {
            clock += 1;
            w.Advance(clock, rel);
            foreach (var e in w.Events)
            {
                if (e.Seq <= lastSeq) continue;
                lastSeq = e.Seq;
                if (w.PlaceById(e.Place) is not { } n) continue;
                // falsifiers, measured from what happened, not from the rule's own code; "took a UC place" = the UC held it last
                int prevHeld = e.B >= 0 ? e.B : lastHeld[n.Id];
                bool onBast = Vector2.Distance(n.Pos, bastCenter) <= bastRadius;
                if (e.Type == EventType.Capture && e.A == fSk && prevHeld == fUc) Fals(w, e, day);
                if (e.Type == EventType.Capture && e.A == fHn && (e.B == fUc || n.OrigOwner == fUc) && !onBast && prevHeld == fUc) Fals(w, e, day);
                if (e.Type == EventType.Capture && n.Kind != PlaceKind.Base && Vector2.Distance(n.Pos, Lore.HubCenter) <= Lore.HubRadius
                    && (e.A == fHn || e.A == fSk))
                {
                    if (e.A == fHn) r.HnHubTook++; else r.SkHubTook++;
                    r.HubLastDay = day;
                }

                if (onBast && e.Type == EventType.Capture && (e.A == fHn || e.A == fUc))
                {
                    if (e.A == fHn) r.HnBastTook++; else r.UcBastTook++;
                    r.BastLastDay = day;
                }

                if (e.Type == EventType.Invasion && e.A == fUc)
                {
                    // as the reference counts them: Blister Hill; anything but Okran's Shield outside Bast; and, unless it is a Bast border
                    // invasion, a place the Holy Nation held strongly
                    if (n.Name == "Blister Hill") Fals(w, e, day);
                    if (n.Name != "Okran's Shield" && !onBast) Fals(w, e, day);
                    if (n.Name != "Okran's Shield" && onBast) { }
                    else if (n.Owner == fHn && w.SettledPower(n.Id, fHn) >= ucWeakBelow) Fals(w, e, day);
                }

                if (e.Type == EventType.Invasion && (e.A == fSt || e.A == fTg)) Fals(w, e, day);
                if (n.Name == "Okran's Shield" && e.A == fUc && e.Type == EventType.Capture && r.UcOsTook == 0) r.UcOsTook = day;
                if (n.Name == "Stack" && e.A == fSk && e.Type == EventType.Capture && r.StackFell == 0) r.StackFell = day;
            }

            foreach (var p in w.Places) if (p.Owner >= 0) lastHeld[p.Id] = p.Owner;
        }

        if (day > days - 30 && os >= 0) r.OsPowLate += w.SettledPower(os, fHn) / 30.0;
        int[] majors = [fHn, fUc, fSk];
        for (int k = 0; k < 3; k++)
            if (majors[k] >= 0 && r.NoBaseDay[k] == 0 && !w.Places.Any(p => p.Owner == majors[k] && p.Kind == PlaceKind.Base)) r.NoBaseDay[k] = day;
        if (day is 30 or 60 or 90)
        {
            int held = 0, total = 0;
            for (int i = 0; i < w.Places.Count; i++)
            {
                if (Vector2.Distance(w.Places[i].Pos, bastCenter) > bastRadius || (owner0[i] != fUc && owner0[i] != fHn)) continue;
                total++;
                if (w.Places[i].Owner == fUc) held++;
            }

            r.UcBast[day / 30 - 1] = held;
            r.BastTotal = total;
        }
    }

    r.Ms = sw.ElapsedMilliseconds;
    r.TookFor = [.. warfare.Fired.TookFor];
    r.PickedFor = [.. warfare.Fired.PickedFor];
    r.NoMotive = warfare.Fired.NoMotive;
    r.CutHours = economy.CutHours;
    r.RaidParties = warfare.Fired.RaidParties;
    if (w.Roads != null) (r.Cutters, r.BandBlocks) = (w.Roads.CuttersFound, w.Roads.BlockedByBand);
    return r;

    // a lore check that failed: counted, and with DL_FALSIFIERS=1 printed with the event behind it
    void Fals(World world, WorldEvent e, int day)
    {
        r.Falsifiers++;
        if (Environment.GetEnvironmentVariable("DL_FALSIFIERS") == "1") Console.WriteLine($"  FALSIFIER seed {s} day {day}: {world.Say(e)}");
    }
}

void Scenario(World w)
{
    // RivalryCore#33's set-ups; each changes only the starting state
    if (scenario.Length == 0) return;
    int fSk = w.FindFaction(kShek), fHn = w.FindFaction(kHn);
    int squin = w.Places.FindIndex(p => p.Name == "Squin"), admag = w.Places.FindIndex(p => p.Name == "Admag");
    if (fSk < 0 || fHn < 0 || squin < 0 || admag < 0) return;
    if (scenario == "stack")
    {
        // the Shek arrive strong: 8 more bands at Squin and Admag, a full store of food and arms
        for (int k = 0; k < 8; k++)
        {
            var at = w.Places[k % 2 == 1 ? admag : squin];
            w.SpawnBand(fSk, at.Id, BandRole.Garrison, at.Pos, "warfare");
        }

        if (Economy.Of(w) is { } e) { e.Food[fSk] += 500; e.Arms[fSk] = 500; }
        return;
    }

    // ease: the Shek already hold the 5 Holy places nearest Squin, 3 bands each; Okran's Shield starts without a garrison
    foreach (var p in w.Places.Where(p => p.Owner == fHn && p.Kind != PlaceKind.Lair).OrderBy(p => Vector2.Distance(p.Pos, w.Places[squin].Pos)).Take(5).ToList())
    {
        foreach (var b in w.Bands.Where(b => b.Alive && b.Faction == fHn && w.Settled(b, p.Id)).ToList()) w.Kill(b);
        p.Owner = fSk;
        for (int k = 0; k < 3; k++) w.SpawnBand(fSk, p.Id, BandRole.Garrison, p.Pos, "warfare");
    }

    int os = w.Places.FindIndex(p => p.Name == "Okran's Shield");
    if (os >= 0) foreach (var b in w.Bands.Where(b => b.Alive && b.Faction == fHn && w.Settled(b, os)).ToList()) w.Kill(b);
}

sealed class RunResult
{
    public ulong Seed;
    public int[] UcBast = [0, 0, 0];
    public int BastTotal, HnBastTook, UcBastTook, BastLastDay, UcOsTook, StackFell, Falsifiers;
    public int[] NoBaseDay = [0, 0, 0];
    public double OsPowLate;
    public long Ms;
    /// <summary>Takings by motive (all None without --motives), and expansion targets passed over for having none.</summary>
    public long[] TookFor = [], PickedFor = [];
    public long NoMotive;
    /// <summary>Place-hours a place produced nothing because its road home was cut.</summary>
    public double CutHours;
    /// <summary>The Hub circle (non-base places): takings by the Holy Nation and the Shek, the last day one changed hands; raid parties sent.</summary>
    public int HnHubTook, SkHubTook, HubLastDay;
    public long RaidParties;
    /// <summary>Places found cutting a faction's road home, and roads closed by a camped band (counted per supply check).</summary>
    public long Cutters, BandBlocks;
}

/// <summary>
/// Logistics Core's edges for the map (made from the game's data with LogisticsEdges, kept outside the repo): E lines with each end's name
/// and position. An end is the map's place of that name within 2,000 units, else the nearest place within 2,000.
/// </summary>
static class RoadFile
{
    public static (int Matched, int Missed) Load(string path, World w)
    {
        var roads = new Roads();
        int matched = 0, missed = 0;
        int Find(string name, float x, float z)
        {
            var at = new Vector2(x, z);
            int best = -1;
            float bd = 2000;
            foreach (var p in w.Places)
            {
                float d = Vector2.Distance(p.Pos, at);
                if (d > 2000) continue;
                if (p.Name == name) return p.Id;
                if (d < bd) (bd, best) = (d, p.Id);
            }

            return best;
        }

        float F(string v) => float.Parse(v, CultureInfo.InvariantCulture);
        foreach (string line in File.ReadLines(path))
        {
            string[] t = line.TrimEnd('\r', '\n').Split('\t');
            if (t.Length < 9 || t[0] != "E") continue;
            int a = Find(t[1], F(t[2]), F(t[3])), b = Find(t[4], F(t[5]), F(t[6]));
            if (a < 0 || b < 0 || roads.Add(a, b, F(t[7]), t[8] == "Overland") < 0) { missed++; continue; }
            matched++;
        }

        w.Roads = roads;
        return (matched, missed);
    }
}

/// <summary>
/// The map file: factions, hostile pairs, factions hostile to the player, and places, as the reference's export wrote them.
/// </summary>
sealed class MapFile : IRelations
{
    readonly List<string[]> _factions = [], _places = [];
    readonly List<(string, string)> _hostile = [];
    readonly HashSet<string> _vsPlayer = [];
    bool[,] _enemy = new bool[0, 0];
    bool[] _player = [];

    public static MapFile Read(string path)
    {
        var m = new MapFile();
        foreach (string line in File.ReadLines(path))
        {
            string[] t = line.TrimEnd('\r', '\n').Split('\t');
            switch (t[0])
            {
                case "F" when t.Length >= 7: m._factions.Add(t); break;
                case "H" when t.Length >= 3: m._hostile.Add((t[1], t[2])); break;
                case "P" when t.Length >= 2: m._vsPlayer.Add(t[1]); break;
                case "N" when t.Length >= 8: m._places.Add(t); break;
            }
        }

        if (m._factions.Count == 0 || m._places.Count == 0) throw new InvalidDataException($"no factions or places in {path}");
        return m;
    }

    public MapFile Build(World w)
    {
        GameMap.Build(w, [.. _factions.Select(f => new MapFaction(f[1], f[2], f[3] == "1", f[4] == "1", f[5] == "1",
            float.Parse(f[6], CultureInfo.InvariantCulture)))], []);
        int n = w.Factions.Count;
        _enemy = new bool[n, n];
        _player = new bool[n];
        foreach (var (a, b) in _hostile)
            if (w.FindFaction(a) is >= 0 and var ia && w.FindFaction(b) is >= 0 and var ib) _enemy[ia, ib] = _enemy[ib, ia] = true;
        for (int i = 0; i < n; i++) _player[i] = _vsPlayer.Contains(w.Factions[i].Key);
        var towns = _places.Select(t => new MapTown(t[1], t[2],
            new Vector2(float.Parse(t[3], CultureInfo.InvariantCulture), float.Parse(t[4], CultureInfo.InvariantCulture)),
            int.Parse(t[5], CultureInfo.InvariantCulture), t[6] == "-" ? null : t[6], t[7] == "1")).ToList();
        GameMap.Build(w, [], towns);
        return this;
    }

    public bool Hostile(int a, int b) => a >= 0 && b >= 0 && a < _player.Length && b < _player.Length && _enemy[a, b];

    public bool HostileToPlayer(int faction) => faction >= 0 && faction < _player.Length && _player[faction];
}
