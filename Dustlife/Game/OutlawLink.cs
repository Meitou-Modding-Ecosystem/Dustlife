// Dustlife (A-Life for Kenshi) for Meitou. Author: PZA. Licence: GPLv3 or later.
using System.Globalization;
using System.Reflection;
using System.Text;
using Dustlife.Core;

namespace Dustlife.Game;

/// <summary>
/// The war pushes The Wasteland Remembers' outlaws back (the interim bridge agreed on Meitou-Modding-Ecosystem/RivalryCore#59 and #126,
/// before War Orchestration exists): a war band on its own faction's ground that walks onto a named outlaw's ground strikes the camp, which
/// TWR's <c>HideoutStruck</c> turns into the outlaw lying low (or licking its wounds). A strike only scatters: death, jail and succession stay
/// TWR's and the player's. TWR's limits, as its author set them:
/// <list type="bullet">
/// <item>never an outlaw the player's squad hasn't heard of (<c>Known</c>);</item>
/// <item>never in the first 7 game days after the outlaw rose or a successor took over (TWR API 5's <c>StrikableAfter</c> when it has it;
/// before that, 7 days from when Dustlife first saw that outlaw run by that person);</item>
/// <item>at most one strike per outlaw per 30 game days;</item>
/// <item>not while the outlaw is wounded, dead, jailed or the player's; and TWR has the last word: a "no" from <c>HideoutStruck</c> is
/// "not now", and Dustlife asks again a day later at the earliest.</item>
/// </list>
/// A test setting, off by default. Nothing throws: a call that fails counts and is skipped.
/// </summary>
public sealed class OutlawLink(Func<Type?>? find = null)
{
    public const string Source = "pza.dustlife";
    public const double GraceHours = 7 * 24, CooldownHours = 30 * 24, NotNowHours = 24;
    /// <summary>A band this close to an outlaw's ground strikes it (TWR's <c>OutlawAt</c> radius).</summary>
    public const float Reach = 1500;
    /// <summary>The band's faction must hold a place this close to it: the outlaw is on or by its own ground (the reason for the strike).</summary>
    public const float OwnGround = 8000;

    readonly Func<Type?> _find = find ?? FindTwr;
    MethodInfo? _outlaws, _at, _struck;
    bool _bound;
    double _lastLook = double.NegativeInfinity;

    readonly Dictionary<string, double> _lastStrike = new(StringComparer.Ordinal), _notNowUntil = new(StringComparer.Ordinal);
    /// <summary>When Dustlife first saw an outlaw run by its current boss (key "outlaw|boss"): the start of its 7 days without TWR's own clock.</summary>
    readonly Dictionary<string, double> _firstSeen = new(StringComparer.Ordinal);

    /// <summary>Counters for the log: strikes TWR took, "not now" answers, and why a band on an outlaw's ground held off.</summary>
    public long Struck, NotNow, Unknown, TooYoung, Cooling, Down, NotOwnGround, Failed;

    public bool Bound => _bound;

    /// <summary>The strike, "not now" and first-seen times kept (for the tests and the log).</summary>
    public int Times => _lastStrike.Count + _notNowUntil.Count + _firstSeen.Count;

    /// <summary>Forgets every kept time (a damaged section in the save, <see cref="LinkSave"/>).</summary>
    public void Clear()
    {
        _lastStrike.Clear();
        _notNowUntil.Clear();
        _firstSeen.Clear();
    }

    static Type? FindTwr() => AppDomain.CurrentDomain.GetAssemblies()
        .FirstOrDefault(a => a.GetName().Name == "LivingWastelandMeitou")?.GetType("LivingWasteland.MeitouMod.WastelandApi");

    bool Bind()
    {
        if (_bound) return true;
        Type? t;
        try { t = _find(); }
        catch (Exception) { return false; }
        if (t == null || t.GetField("Version", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is not int v || v < 4) return false;
        const BindingFlags pub = BindingFlags.Public | BindingFlags.Static;
        _outlaws = t.GetMethod("Outlaws", pub, Type.EmptyTypes);
        _at = t.GetMethod("OutlawAt", pub, [typeof(float), typeof(float), typeof(float)]);
        _struck = t.GetMethod("HideoutStruck", pub, [typeof(string), typeof(string), typeof(bool), typeof(string)]);
        _bound = _outlaws != null && _at?.ReturnType == typeof(string) && _struck?.ReturnType == typeof(bool);
        return _bound;
    }

    static T Get<T>(IReadOnlyDictionary<string, object> row, string key, T fallback) => row.TryGetValue(key, out var o) && o is T x ? x : fallback;

    static double Number(IReadOnlyDictionary<string, object> row, string key) =>
        row.TryGetValue(key, out var o) ? o switch { double d => d, float f => f, int i => i, long l => l, _ => double.NaN } : double.NaN;

    /// <summary>
    /// Once a game hour at most: the war's bands on an outlaw's ground strike its camp, within TWR's limits. At most one strike per call.
    /// Returns the line to log for a strike, or null.
    /// </summary>
    public string? Step(World w, double now)
    {
        if (now - _lastLook < 1) return null;
        _lastLook = now;
        if (!Bind()) return null;
        Dictionary<string, IReadOnlyDictionary<string, object>> rows;
        try
        {
            rows = new(StringComparer.Ordinal);
            if (_outlaws!.Invoke(null, null) is IEnumerable<IReadOnlyDictionary<string, object>> list)
                foreach (var r in list)
                    if (Get(r, "Name", "") is { Length: > 0 } n) rows[n] = r;
        }
        catch (Exception)
        {
            Failed++;
            return null;
        }

        foreach (var (name, row) in rows)                                                  // start each outlaw's clock the first time it is seen
        {
            string key = name + "|" + Boss(name, row);
            _firstSeen.TryAdd(key, now);
        }

        foreach (var b in w.Bands)
        {
            if (!b.Alive || b.External || b.Owner != "warfare" || b.Role is not (BandRole.Patrol or BandRole.Invader)) continue;
            string name;
            try
            {
                name = _at!.Invoke(null, [b.Pos.X, b.Pos.Y, Reach]) as string ?? "";
            }
            catch (Exception)
            {
                Failed++;
                return null;
            }

            if (name.Length == 0 || !rows.TryGetValue(name, out var row)) continue;
            if (!MayStrike(w, now, b, name, row)) continue;
            string by = w.Factions[b.Faction].Name;
            bool bloodied = b.Role == BandRole.Invader;
            bool took;
            try
            {
                took = _struck!.Invoke(null, [name, by, bloodied, Source]) is true;
            }
            catch (Exception)
            {
                Failed++;
                return null;
            }

            if (!took)
            {
                NotNow++;
                _notNowUntil[name] = now + NotNowHours;
                continue;
            }

            Struck++;
            _lastStrike[name] = now;
            return FormattableString.Invariant($"{by}'s {(bloodied ? "war band" : "patrol")} struck {name}'s camp; the outlaw scatters (TWR decides how)");
        }

        return null;
    }

    static string Boss(string name, IReadOnlyDictionary<string, object> row) =>
        Get(row, "Successor", "") is { Length: > 0 } s ? s : name;

    bool MayStrike(World w, double now, Band b, string name, IReadOnlyDictionary<string, object> row)
    {
        string status = Get(row, "Status", "");
        if (status is not ("alive" or "succeeded") || Get(row, "Mood", "") == "wounded")
        {
            Down++;
            return false;
        }

        if (!Get(row, "Known", false))
        {
            Unknown++;
            return false;
        }

        double after = Number(row, "StrikableAfter");                                      // TWR API 5
        double since = _firstSeen.GetValueOrDefault(name + "|" + Boss(name, row), now);
        if (double.IsFinite(after) ? now < after : now - since < GraceHours)
        {
            TooYoung++;
            return false;
        }

        if ((_lastStrike.TryGetValue(name, out double last) && now - last < CooldownHours) ||
            (_notNowUntil.TryGetValue(name, out double until) && now < until))
        {
            Cooling++;
            return false;
        }

        if (!w.Places.Any(p => p.Owner == b.Faction && System.Numerics.Vector2.Distance(p.Pos, b.Pos) <= OwnGround))
        {
            NotOwnGround++;
            return false;
        }

        return true;
    }

    public string Summary() => _bound
        ? FormattableString.Invariant($"outlaw camps: {Struck} struck, {NotNow} refused by TWR (not now); held off: {Unknown} not yet heard of, {TooYoung} too new, {Cooling} struck recently, {Down} wounded or down, {NotOwnGround} away from their own ground; {Failed} failed")
        : "outlaw camps: The Wasteland Remembers' outlaws (API 4+) not found";

    #region Saving

    const uint SaveMagic = 0x4F4C_4C44;                                          // "DLLO"
    const int SaveVersion = 1;
    const int MaxSaved = 10000;

    /// <summary>The strike times and first sightings, after the TWR link's section: a reload must not hand out a second strike early.</summary>
    public void Write(Stream stream)
    {
        using var bw = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        bw.Write(SaveMagic);
        bw.Write(SaveVersion);
        foreach (var d in new[] { _lastStrike, _notNowUntil, _firstSeen })
        {
            var rows = d.OrderBy(e => e.Key, StringComparer.Ordinal).Take(MaxSaved).ToList();
            bw.Write(rows.Count);
            foreach (var (k, v) in rows)
            {
                bw.Write(k);
                bw.Write(v);
            }
        }
    }

    /// <summary>Reads what <see cref="Write"/> wrote; a save from before has nothing here. Times that are not finite are dropped.</summary>
    public int Read(Stream stream)
    {
        _lastStrike.Clear();
        _notNowUntil.Clear();
        _firstSeen.Clear();
        if (stream.Length - stream.Position < 8) return 0;
        using var br = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (br.ReadUInt32() != SaveMagic || br.ReadInt32() != SaveVersion) return 0;
        var parts = new List<List<(string, double)>>();
        for (int part = 0; part < 3; part++)
        {
            int count = br.ReadInt32();
            if (count < 0 || count > MaxSaved) throw new InvalidDataException($"{count} saved outlaw times");
            var rows = new List<(string, double)>();
            for (int i = 0; i < count; i++) rows.Add((WastelandLink.Text(br), br.ReadDouble()));
            parts.Add(rows);
        }

        int kept = 0;
        foreach (var (d, rows) in new[] { _lastStrike, _notNowUntil, _firstSeen }.Zip(parts))
            foreach (var (k, v) in rows)
                if (double.IsFinite(v) && k.Length > 0) { d[k] = v; kept++; }
        return kept;
    }

    #endregion
}
