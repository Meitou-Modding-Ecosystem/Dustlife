// Dustlife (A-Life for Kenshi) for Meitou. Author: PZA. Licence: GPLv3 or later.
using Dustlife.Core;

namespace Dustlife.Game;

/// <summary>
/// What a placed town is now in the game: the override version it took (empty: its own record) and its holder's faction key.
/// </summary>
public readonly record struct TownVersion(string Version, string? Faction);

/// <summary>
/// Notices when the game changes a town (port step 5): an <c>override town</c> taking its place when its conditions come true (Esata
/// dies, and Stack becomes the Shek's), at its zone load on the 0.20 engine or at once with the off-screen overrides of newer ones. It
/// compares each place's town with what it saw last and, on a change, gives the place to the new version's holder and says so in the
/// news. A town that changes back is a change too. It works on every engine because it only reads the towns.
/// </summary>
public sealed class TownWatch
{
    readonly Dictionary<string, TownVersion> _seen = [];

    /// <summary>Switches seen and applied (a rule that never fires is a failed test).</summary>
    public long Switches { get; private set; }

    /// <summary>
    /// Remembers the towns as they are, without acting (a new world, or a loaded one: its save already holds who holds what).
    /// </summary>
    public void Prime(IEnumerable<(string Key, TownVersion Now)> towns)
    {
        _seen.Clear();
        foreach (var (key, now) in towns) _seen[key] = now;
    }

    /// <summary>
    /// Compares the towns with what was seen last and applies each change to <paramref name="w"/>: the place's holder becomes the new
    /// version's faction (nobody when that faction is not in the world), its bands that are not the new holder's leave for home or the
    /// nearest place of theirs, and the news says it. Returns the places that changed.
    /// </summary>
    public List<int> Poll(World w, IEnumerable<(string Key, TownVersion Now)> towns, IRelations rel)
    {
        var changed = new List<int>();
        foreach (var (key, now) in towns)
        {
            if (!_seen.TryGetValue(key, out var was)) { _seen[key] = now; continue; }
            if (was == now) continue;
            _seen[key] = now;
            int id = w.FindPlace(key);
            if (id < 0) continue;
            Apply(w, w.Places[id], now, rel);
            changed.Add(id);
        }

        return changed;
    }

    void Apply(World w, Place p, TownVersion now, IRelations rel)
    {
        int from = p.Owner;
        int to = now.Faction is null ? -1 : w.FindFaction(now.Faction);
        Switches++;
        p.Owner = to;
        foreach (var b in w.Bands)
        {
            if (!b.Alive || b.Faction == to || !w.Settled(b, p.Id)) continue;
            int home = w.Places.Where(x => x.Owner == b.Faction && x.Id != p.Id)
                .OrderBy(x => System.Numerics.Vector2.DistanceSquared(x.Pos, p.Pos)).Select(x => x.Id).DefaultIfEmpty(-1).First();
            if (home >= 0) { b.Home = home; w.Send(b, home, BandRole.Garrison); }
            else w.Kill(b);                                                  // nowhere left to go: the band breaks up
        }

        string fromName = from >= 0 ? w.Factions[from].Name : "nobody";
        if (to >= 0) w.Emit(EventType.Capture, to, from, p.Id, p.Pos, 3, "town_changed", p.Name, w.Factions[to].Name, fromName);
        else w.Emit(EventType.Lost, -1, from, p.Id, p.Pos, 3, "town_emptied", p.Name, fromName);
        _ = rel;
    }
}
