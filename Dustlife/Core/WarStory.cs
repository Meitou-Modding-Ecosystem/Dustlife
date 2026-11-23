// Dustlife (A-Life for Kenshi) for Meitou. Author: PZA. Licence: GPLv3 or later.
using System.Globalization;
using System.Text;

namespace Dustlife.Core;

/// <summary>
/// The war told as a story: the event stream grouped into arcs, one per
/// attacker and target (a march, its siege, how it ended), and the story metrics the design is judged by. It only reads events, so it
/// changes nothing in the war; the balance runner and the headless runs print it.
/// </summary>
public sealed class WarStory
{
    /// <summary>How an arc ended.</summary>
    public enum Outcome { Open, Fell, Overrun, GivenUp, Broken, Faded }

    /// <summary>One attacker's try at one place.</summary>
    public sealed class Arc
    {
        public int Attacker, Defender, Place;
        public string AttackerName = "", DefenderName = "", PlaceName = "", From = "", Leader = "";
        public int Bands;
        public double Start, SiegeStart = -1, End = -1;
        public Outcome Result;
        public bool Raid;
        /// <summary>A town or a resource place (the siege targets of the design are for these; ruins fall easily by design).</summary>
        public bool Town;

        /// <summary>Siege length in game hours, or -1 without a siege or an end.</summary>
        public double SiegeHours => SiegeStart >= 0 && End >= SiegeStart ? End - SiegeStart : -1;
    }

    /// <summary>A place an arc no longer hears of for this long has faded (game hours).</summary>
    public const double FadeHours = 240;

    /// <summary>The window in which a place changing hands again counts as a flip-back (game hours).</summary>
    public const double FlipHours = 7 * 24;

    readonly Dictionary<(int, int), Arc> _open = [];
    readonly List<Arc> _done = [];
    readonly Dictionary<int, (int Owner, double Time)> _lastCapture = [];
    readonly Dictionary<int, double> _warlordSince = [];
    readonly List<string> _lines = [];
    long _seq = -1;

    /// <summary>Captures and flip-backs (a place taken again by another faction within <see cref="FlipHours"/>).</summary>
    public int Captures, FlipBacks;

    /// <summary>F1 rhythm: wars worn out and wars renewed.</summary>
    public int Exhaustions, Renewals;

    /// <summary>Who wiped out besieging bands near their siege: the place's holder, a third faction, the wildlife, or unknown.</summary>
    public int BrokenByDefender, BrokenByThird, BrokenByFauna, BrokenByNone;

    /// <summary>Each warlord's time in command (game hours), counted when they were replaced.</summary>
    public List<double> WarlordTenures { get; } = [];

    public IReadOnlyList<Arc> Done => _done;

    /// <summary>Reads the world's new events (call after each world step).</summary>
    public void Read(World w)
    {
        foreach (var e in w.Events)
        {
            if (e.Seq <= _seq) continue;
            _seq = e.Seq;
            On(w, e);
        }

        foreach (var (k, a) in _open.ToList())
        {
            // a siege whose besiegers are all gone, with no event for it, was broken (they fell in the fight at the line)
            if (a.SiegeStart >= 0 && !w.Bands.Any(b => b.Alive && b.Faction == a.Attacker && (b.Target == a.Place || b.Home == a.Place)))
            {
                Close(k, a, Outcome.Broken, w.Now);
                _lines.Add($"Day {Day(w.Now)}: the siege of {a.PlaceName} by {a.AttackerName} was broken.");
            }
            else if (w.Now - Math.Max(a.Start, a.SiegeStart) > FadeHours) Close(k, a, Outcome.Faded, w.Now);
        }
    }

    static string Day(double hours) => (1 + (int)(hours / 24)).ToString(CultureInfo.InvariantCulture);

    string FactionName(World w, int f) => f >= 0 && f < w.Factions.Count ? w.Factions[f].Name : "nobody";

    void On(World w, WorldEvent e)
    {
        var place = w.PlaceById(e.Place);
        switch (e.Type)
        {
            case EventType.Invasion when place != null && e.A >= 0:
            {
                var key = (e.A, e.Place);
                if (_open.ContainsKey(key)) return;
                bool raid = e.Key == "raid";
                var arc = new Arc
                {
                    Attacker = e.A, Defender = e.B, Place = e.Place, AttackerName = FactionName(w, e.A), DefenderName = FactionName(w, e.B),
                    PlaceName = place.Name, Start = e.Time, Raid = raid, Town = place.Kind is PlaceKind.Base or PlaceKind.Resource,
                    // march: {1} faction, {2} target, {3} from, {4} bands, {5} leader; raid: {3} from, {4} bands, {5} leader
                    From = e.Key is "march" or "raid" && e.Args.Count > 2 ? e.Args[2] : "",
                    Bands = e.Key is "march" or "raid" && e.Args.Count > 3 && int.TryParse(e.Args[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 0,
                    Leader = e.Key is "march" or "raid" && e.Args.Count > 4 ? e.Args[4] : "",
                };
                _open[key] = arc;
                _lines.Add($"Day {Day(e.Time)}: {w.Say(e)}");
                return;
            }
            case EventType.SiegeBegan when place != null && _open.TryGetValue((e.A, e.Place), out var arc):
                if (arc.SiegeStart < 0) arc.SiegeStart = e.Time;
                _lines.Add($"Day {Day(e.Time)}: {w.Say(e)}");
                return;
            case EventType.Rout when e.Key == "siege_end" && place != null && _open.TryGetValue((e.B, e.Place), out var given):
                // only when no band of that attacker still besieges the place
                if (w.Bands.Any(b => b.Alive && b.Faction == e.B && b.State == BandState.Siege && b.Target == e.Place)) return;
                Close((e.B, e.Place), given, Outcome.GivenUp, e.Time);
                _lines.Add($"Day {Day(e.Time)}: {w.Say(e)}");
                return;
            case EventType.Lost when e.Key == "overrun" && place != null:
                foreach (var (k, a) in _open.Where(x => x.Key.Item2 == e.Place).ToList()) a.Result = Outcome.Overrun;
                _lines.Add($"Day {Day(e.Time)}: {w.Say(e)}");
                return;
            case EventType.Capture when place != null && e.A >= 0:
            {
                Captures++;
                if (_lastCapture.TryGetValue(e.Place, out var last) && last.Owner != e.A && e.Time - last.Time <= FlipHours) FlipBacks++;
                _lastCapture[e.Place] = (e.A, e.Time);
                if (_open.TryGetValue((e.A, e.Place), out var won)) Close((e.A, e.Place), won, won.Result == Outcome.Overrun ? Outcome.Overrun : Outcome.Fell, e.Time);
                foreach (var (k, a) in _open.Where(x => x.Key.Item2 == e.Place && x.Key.Item1 != e.A).ToList()) Close(k, a, Outcome.Faded, e.Time);
                if (e.Importance >= 2) _lines.Add($"Day {Day(e.Time)}: {w.Say(e)}");
                return;
            }
            case EventType.Warlord when e.Key != "warlord_escaped":                  // the same person riding out of a rout is no change of command
            {
                int f = e.A;
                if (f >= 0 && _warlordSince.TryGetValue(f, out double since)) WarlordTenures.Add(e.Time - since);
                if (f >= 0) _warlordSince[f] = e.Time;
                _lines.Add($"Day {Day(e.Time)}: {w.Say(e)}");
                return;
            }
            case EventType.Info when e.Key is "war_exhausted" or "war_truce" or "war_renewed" or "siege_starving" or "siege_ready":
                if (e.Key == "war_exhausted") Exhaustions++;
                if (e.Key == "war_renewed") Renewals++;
                _lines.Add($"Day {Day(e.Time)}: {w.Say(e)}");
                return;
            case EventType.Wiped when e.B >= 0:
                foreach (var a in _open.Values)
                {
                    if (a.Attacker != e.B || a.SiegeStart < 0 || w.PlaceById(a.Place) is not { } sp || System.Numerics.Vector2.Distance(sp.Pos, e.Pos) > 5000) continue;
                    if (e.A < 0) BrokenByNone++;
                    else if (e.A == sp.Owner || e.A == a.Defender) BrokenByDefender++;
                    else if (e.A < w.Factions.Count && w.Factions[e.A].Fauna) BrokenByFauna++;
                    else BrokenByThird++;
                    break;
                }
                return;
            case EventType.LeaderDied when e.Importance >= 2:
                _lines.Add($"Day {Day(e.Time)}: {w.Say(e)}");
                return;
        }
    }

    void Close((int, int) key, Arc a, Outcome how, double at)
    {
        _open.Remove(key);
        a.Result = how;
        a.End = at;
        _done.Add(a);
    }

    /// <summary>The story so far, line by line, oldest first.</summary>
    public IReadOnlyList<string> Lines => _lines;

    /// <summary>The arcs as short paragraphs (the closed ones), for a reader.</summary>
    public string Render()
    {
        var sb = new StringBuilder();
        foreach (var a in _done.OrderBy(x => x.Start))
        {
            string end = a.Result switch
            {
                Outcome.Fell => $"took it on day {Day(a.End)}",
                Outcome.Overrun => $"overran it on day {Day(a.End)}",
                Outcome.GivenUp => $"gave up on day {Day(a.End)}",
                Outcome.Broken => $"were broken at its walls by day {Day(a.End)}",
                _ => $"heard of no more (day {Day(a.End)})",
            };
            string siege = a.SiegeStart >= 0 ? $", besieged it from day {Day(a.SiegeStart)}" : "";
            string who = a.Leader.Length > 0 ? $" under {a.Leader}" : "";
            sb.Append(CultureInfo.InvariantCulture, $"Day {Day(a.Start)}: {a.AttackerName}{who} {(a.Raid ? "raided" : "marched on")} {a.PlaceName}");
            if (a.From.Length > 0) sb.Append(CultureInfo.InvariantCulture, $" from {a.From}");
            sb.Append(CultureInfo.InvariantCulture, $"{siege}, and {end}.\n");
        }

        return sb.ToString();
    }

    /// <summary>The story metrics (section 12 of the design), from the closed arcs: sieges of towns and resource places (not raids, not ruins).</summary>
    public (int Arcs, int Sieges, double SiegeDaysMedian, double FallShare, int Captures, int FlipBacks, double WarlordDaysMedian) Metrics()
    {
        var sieges = _done.Where(a => a.SiegeHours >= 0 && a.Town && !a.Raid).ToList();   // towns and resource places, not raids
        double Median(IEnumerable<double> xs)
        {
            var v = xs.OrderBy(x => x).ToList();
            return v.Count == 0 ? double.NaN : v.Count % 2 == 1 ? v[v.Count / 2] : 0.5 * (v[v.Count / 2 - 1] + v[v.Count / 2]);
        }

        int fell = sieges.Count(a => a.Result is Outcome.Fell or Outcome.Overrun);
        return (_done.Count, sieges.Count, Median(sieges.Select(a => a.SiegeHours / 24)), sieges.Count == 0 ? double.NaN : (double)fell / sieges.Count,
            Captures, FlipBacks, Median(WarlordTenures.Select(t => t / 24)));
    }
}
