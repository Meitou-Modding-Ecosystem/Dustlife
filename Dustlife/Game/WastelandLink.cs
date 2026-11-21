// Dustlife (A-Life for Kenshi) for Meitou. Author: PZA. Licence: GPLv3 or later.
using System.Reflection;
using Dustlife.Core;

namespace Dustlife.Game;

/// <summary>
/// Dustlife's war news to The Wasteland Remembers' news board, so the towns near a fight talk about it. TWR's <c>WastelandApi</c> is
/// read by reflection (TWR's <c>interop/MODDER_API.md</c>), so neither mod depends on the other: with TWR missing, too old or not ready,
/// nothing is sent and nothing throws. A place goes by its node <c>town:&lt;placement id&gt;</c> (<c>ReportAtNode</c>, TWR's API 4),
/// else by its name (<c>Report</c>, API 2). Sent: a place taken from another faction ("town-fell"), a siege given up ("siege-broken"), and
/// a march or raid on a place (with Dustlife's own line). Not sent: the game's own town changes (TWR sees those itself), places settled
/// while empty, and events with no place. TWR takes the same kind at the same place once in 12 game hours, whoever reports it.
/// </summary>
public sealed class WastelandLink(Func<Type?>? find = null)
{
    public const string Source = "dustlife";
    /// <summary>At most this many reports per world step (a big battle day must not drown the board).</summary>
    public const int MaxPerStep = 6;

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

    /// <summary>Sends one step's new events (oldest first); returns how many TWR took.</summary>
    public int Forward(World w, IEnumerable<WorldEvent> fresh)
    {
        int sent = 0, tried = 0;
        foreach (var e in fresh)
        {
            if (Map(w, e) is not { } m)
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
            string faction = m.Kind == "town-fell" && e.A >= 0 && e.A < w.Factions.Count ? w.Factions[e.A].Name : "";
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
