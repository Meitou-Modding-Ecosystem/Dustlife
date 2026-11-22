// Dustlife (A-Life for Kenshi) for Meitou. Author: PZA. Licence: GPLv3 or later.
using System.Reflection;
using System.Text;
using Dustlife.Core;

namespace Dustlife.Game;

/// <summary>
/// Dustlife's war news to The Wasteland Remembers' news board, so the towns near a fight talk about it. TWR's <c>WastelandApi</c> is
/// read by reflection (TWR's <c>interop/MODDER_API.md</c>), so neither mod depends on the other: with TWR missing, too old or not ready,
/// nothing is sent and nothing throws. A place goes by its node <c>town:&lt;placement id&gt;</c> (<c>ReportAtNode</c>, TWR's API 4),
/// else by its name (<c>Report</c>, API 2). Sent: a place taken from another faction ("town-fell"), a siege given up ("siege-broken"), and
/// a march or raid on a place (with Dustlife's own line). Most falls come in two steps, the defenders overrun and the place settled by the
/// winner a little later: that settling is sent as the fall when it comes within <see cref="FallHours"/> of the overrun and the settler is
/// not the old holder. Not sent: the game's own town changes (TWR sees those itself), the overrun alone, empty places settled with no
/// overrun before, and events with no place. TWR takes the same kind at the same place once in 12 game hours, whoever reports it.
/// </summary>
public sealed class WastelandLink(Func<Type?>? find = null)
{
    public const string Source = "dustlife";
    /// <summary>At most this many reports per world step (a big battle day must not drown the board).</summary>
    public const int MaxPerStep = 6;
    /// <summary>A place settled within this many game hours of its defenders being overrun has fallen to the settler.</summary>
    public const double FallHours = 72;

    /// <summary>Places whose defenders were overrun: when, and who held them (Dustlife's faction index).</summary>
    readonly Dictionary<int, (double Hours, int Holder)> _overrun = [];

    readonly Func<Type?> _find = find ?? FindTwr;
    MethodInfo? _atNode, _byName;
    bool _bound;

    /// <summary>Counters for the log: sent, refused by TWR, skipped (not a kind TWR hears), held back (over the cap), failed (threw).</summary>
    public int Sent, Refused, Skipped, Capped, Failed;

    /// <summary>Whether TWR's API was found (looked up again on each step until it is: assemblies load in any order).</summary>
    public bool Bound => _bound;

    static Type? FindTwr() => AppDomain.CurrentDomain.GetAssemblies()
        .FirstOrDefault(a => a.GetName().Name == "LivingWastelandMeitou")?.GetType("LivingWasteland.MeitouMod.WastelandApi");

    bool Bind()
    {
        if (_bound) return true;
        Type? t;
        try { t = _find(); }
        catch (Exception) { return false; }
        if (t == null) return false;
        if (t.GetField("Version", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is not int v || v < 2) return false;
        Type[] node = [typeof(string), typeof(string), typeof(string), typeof(string), typeof(int), typeof(string)];
        _atNode = v >= 4 ? Typed(t.GetMethod("ReportAtNode", BindingFlags.Public | BindingFlags.Static, node)) : null;
        _byName = Typed(t.GetMethod("Report", BindingFlags.Public | BindingFlags.Static, node));
        _bound = _atNode != null || _byName != null;
        return _bound;
    }

    static MethodInfo? Typed(MethodInfo? m) => m?.ReturnType == typeof(bool) ? m : null;

    /// <summary>What TWR hears of an event: its kind, the line (empty when TWR has its own for the kind) and the weight; null for none.</summary>
    public static (string Kind, string Text, int Weight)? Map(World w, WorldEvent e)
    {
        if (w.PlaceById(e.Place) == null) return null;
        int weight = e.Importance >= 3 ? 80 : e.Importance == 2 ? 65 : 45;
        return e.Type switch
        {
            EventType.Capture when e.B >= 0 && e.Key.StartsWith("took", StringComparison.Ordinal) => ("town-fell", "", weight),
            EventType.Rout when e.Key == "siege_end" => ("siege-broken", "", 55),
            EventType.Invasion when e.Key is "march" or "raid" => ("war-" + e.Key, w.Say(e), weight),
            _ => null,
        };
    }

    /// <summary>A place settled soon after its defenders were overrun, by someone else than its old holder: the fall, or null.</summary>
    (string Kind, string Text, int Weight)? SettledAfterOverrun(World w, WorldEvent e)
    {
        if (e.Type != EventType.Capture || e.B >= 0 || !e.Key.StartsWith("hold", StringComparison.Ordinal)) return null;
        if (!_overrun.Remove(e.Place, out var o) || e.Time - o.Hours > FallHours || e.Time < o.Hours || o.Holder == e.A) return null;
        if (w.PlaceById(e.Place) == null) return null;
        return ("town-fell", "", e.Importance >= 3 ? 80 : e.Importance == 2 ? 65 : 45);
    }

    #region Saving

    const uint SaveMagic = 0x5754_4C44;                                          // "DLTW"
    const int SaveVersion = 1;
    const int MaxSaved = 10000;

    /// <summary>
    /// The overruns still waiting for their settling, by the place's and the old holder's stable keys (placement id, faction id) and the game
    /// hour, so a fall that straddles a save and load is reported as in uninterrupted play (prd's review of Dustlife #2).
    /// </summary>
    public void Write(Stream stream, World w)
    {
        using var bw = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        var rows = _overrun.Where(e => w.PlaceById(e.Key) != null).OrderBy(e => e.Key).Take(MaxSaved).ToList();
        bw.Write(SaveMagic);
        bw.Write(SaveVersion);
        bw.Write(rows.Count);
        foreach (var (place, (hours, holder)) in rows)
        {
            bw.Write(w.PlaceById(place)!.Key);
            bw.Write(holder >= 0 && holder < w.Factions.Count ? w.Factions[holder].Key : "");
            bw.Write(hours);
        }
    }

    /// <summary>
    /// Reads what <see cref="Write"/> wrote; a save from before 0.4.5 has nothing here and keeps none. Places and factions no longer in the world,
    /// and times that are not finite, are dropped. Returns how many were kept.
    /// </summary>
    public int Read(Stream stream, World w)
    {
        _overrun.Clear();
        if (stream.Length - stream.Position < 12) return 0;
        using var br = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (br.ReadUInt32() != SaveMagic || br.ReadInt32() != SaveVersion) return 0;
        int count = br.ReadInt32();
        if (count < 0 || count > MaxSaved) throw new InvalidDataException($"{count} saved overruns");
        var rows = new List<(string Place, string Holder, double Hours)>();
        for (int i = 0; i < count; i++) rows.Add((Text(br), Text(br), br.ReadDouble()));
        foreach (var (placeKey, holderKey, hours) in rows)
        {
            if (!double.IsFinite(hours) || w.Places.Find(p => p.Key == placeKey) is not { } p) continue;
            int holder = holderKey.Length == 0 ? -1 : w.Factions.FindIndex(f => f.Key == holderKey);
            if (holderKey.Length > 0 && holder < 0) continue;
            _overrun[p.Id] = (hours, holder);
        }

        return _overrun.Count;
    }

    /// <summary>A saved string; a damaged length (BinaryReader's FormatException) is a damaged save, as the system catches it.</summary>
    internal static string Text(BinaryReader br)
    {
        try
        {
            return br.ReadString();
        }
        catch (FormatException e)
        {
            throw new InvalidDataException("damaged text in the save: " + e.Message, e);
        }
    }

    /// <summary>The overruns waiting for their settling (for the tests and the log).</summary>
    public int Pending => _overrun.Count;

    #endregion

    /// <summary>Sends one step's new events (oldest first); returns how many TWR took.</summary>
    public int Forward(World w, IEnumerable<WorldEvent> fresh)
    {
        int sent = 0, tried = 0;
        foreach (var e in fresh)
        {
            if (e.Type == EventType.Lost && e.Key == "overrun" && e.Place >= 0)
            {
                _overrun[e.Place] = (e.Time, e.B);
                Skipped++;
                continue;
            }

            var mapped = Map(w, e);
            int taker = e.A;
            if (mapped == null && SettledAfterOverrun(w, e) is { } fell) mapped = fell;
            if (mapped is not { } m)
            {
                Skipped++;
                continue;
            }

            if (!Bind()) return sent;                                              // TWR missing or too old: try again next step
            if (tried >= MaxPerStep)
            {
                Capped++;
                continue;
            }

            tried++;
            var p = w.PlaceById(e.Place)!;
            string faction = m.Kind == "town-fell" && taker >= 0 && taker < w.Factions.Count ? w.Factions[taker].Name : "";
            try
            {
                bool ok = _atNode != null
                    ? (bool)_atNode.Invoke(null, ["town:" + p.Key, m.Kind, faction, m.Text, m.Weight, Source])!
                    : (bool)_byName!.Invoke(null, [m.Kind, p.Name, faction, m.Text, m.Weight, Source])!;
                if (ok)
                {
                    Sent++;
                    sent++;
                }
                else Refused++;
            }
            catch (Exception)
            {
                Failed++;
            }
        }

        return sent;
    }
}
