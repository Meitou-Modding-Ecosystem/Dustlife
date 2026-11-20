// Dustlife (A-Life for Kenshi) for Meitou. Author: PZA. Licence: GPLv3 or later.
using System.Numerics;

namespace Dustlife.Core;

/// <summary>A road between two places (Logistics Core's edge: the game's own route, a short straight walk, or an overland join).</summary>
public sealed record Road(int A, int B, float Cost, bool Overland = false);

/// <summary>
/// The places' road graph (port step 4): Logistics Core's edges, by place index. It answers how far apart two places are along the roads,
/// and which of a faction's places are still joined to one of its bases by roads its enemies don't block (supply). Built once per world
/// and kept out of the save: the same world gives the same roads. A world without roads (null) measures straight, as the reference did.
/// </summary>
public sealed class Roads
{
    readonly List<Road> _roads = [];
    readonly List<List<(int To, int Road)>> _adj = [];
    readonly Dictionary<int, float[]> _from = [];

    public IReadOnlyList<Road> All => _roads;

    /// <summary>A camped hostile band this close to a road (world units) blocks it.</summary>
    public float BlockDist = 1500;

    /// <summary>Supply refusals and their causes, counted by <see cref="Supplied"/>, and places found cutting a road home by
    /// <see cref="Cutters"/> (a rule that never fires is a failed test).</summary>
    public long CutOff, BlockedByPlace, BlockedByBand, CuttersFound;

    /// <summary>
    /// Adds a road; a road to itself, a duplicate pair, or a bad cost is ignored. Returns its index, or -1.
    /// </summary>
    public int Add(int a, int b, float cost, bool overland = false)
    {
        if (a < 0 || b < 0 || a == b || !World.Finite(cost) || cost < 0) return -1;
        Grow(Math.Max(a, b) + 1);
        if (_adj[a].Any(e => e.To == b)) return -1;
        _roads.Add(new Road(a, b, cost, overland));
        int i = _roads.Count - 1;
        _adj[a].Add((b, i));
        _adj[b].Add((a, i));
        _from.Clear();
        return i;
    }

    void Grow(int n)
    {
        while (_adj.Count < n) _adj.Add([]);
    }

    /// <summary>The roads at a place.</summary>
    public IReadOnlyList<(int To, int Road)> At(int place) => place >= 0 && place < _adj.Count ? _adj[place] : [];

    /// <summary>
    /// The length along the roads from <paramref name="a"/> to <paramref name="b"/>, or +infinity when no road joins them. Shortest paths
    /// from a place are kept until a road is added.
    /// </summary>
    public float Distance(int a, int b)
    {
        if (a == b) return 0;
        if (a < 0 || b < 0 || a >= _adj.Count || b >= _adj.Count) return float.PositiveInfinity;
        if (!_from.TryGetValue(a, out var d))
        {
            d = Dijkstra(a);
            _from[a] = d;
        }

        return d[b];
    }

    float[] Dijkstra(int src)
    {
        var d = new float[_adj.Count];
        Array.Fill(d, float.PositiveInfinity);
        d[src] = 0;
        var q = new PriorityQueue<int, float>();
        q.Enqueue(src, 0);
        while (q.TryDequeue(out int u, out float du))
        {
            if (du > d[u]) continue;
            foreach (var (v, r) in _adj[u])
            {
                float nd = du + _roads[r].Cost;
                if (nd < d[v]) { d[v] = nd; q.Enqueue(v, nd); }
            }
        }

        return d;
    }

    /// <summary>
    /// Whether a place stops a faction's supply passing through it: an enemy holds it, or enemies besiege it.
    /// </summary>
    static bool BlocksPlace(World w, int faction, Place p, HashSet<int> besiegedByEnemy, IRelations rel) =>
        (p.Owner >= 0 && p.Owner != faction && w.Hostile(faction, p.Owner, rel)) || besiegedByEnemy.Contains(p.Id);

    /// <summary>
    /// Whether a camped band of the faction's enemies sits on the road (settled or besieging, within <see cref="BlockDist"/> of the straight
    /// line between its ends: Logistics Core's routes bend, so this is the near part of the road).
    /// </summary>
    bool BlocksRoad(World w, int faction, Road r, List<Band> camped, IRelations rel)
    {
        var a = w.Places[r.A].Pos;
        var b = w.Places[r.B].Pos;
        foreach (var band in camped)
        {
            if (band.Faction == faction || !w.Hostile(faction, band.Faction, rel)) continue;
            if (Vector2.Distance(band.Pos, a) < BlockDist || Vector2.Distance(band.Pos, b) < BlockDist) continue;   // at a town, not the road
            if (SegmentDist(band.Pos, a, b) <= BlockDist) return true;
        }

        return false;
    }

    static float SegmentDist(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float len = ab.LengthSquared();
        float t = len > 0 ? Math.Clamp(Vector2.Dot(p - a, ab) / len, 0, 1) : 0;
        return Vector2.Distance(p, a + t * ab);
    }

    /// <summary>
    /// Which places a faction can reach from its bases by roads its enemies don't block: it walks out from every base the faction (or, with
    /// <paramref name="members"/>, any faction of its bloc) holds, through places that are friendly or empty, along roads no hostile band is
    /// camped on. A place outside the result is cut off. Without bases nothing is supplied. Without roads, everything is.
    /// </summary>
    public bool[] Supplied(World w, int faction, IRelations rel, Func<int, bool>? members = null)
    {
        var ok = new bool[w.Places.Count];
        members ??= f => f == faction;
        var besieged = new HashSet<int>(w.Bands.Where(b => b.Alive && b.State == BandState.Siege && b.Target >= 0
            && b.Faction != faction && w.Hostile(faction, b.Faction, rel)).Select(b => b.Target));
        var camped = w.Bands.Where(b => b.Alive && b.State is BandState.Settled or BandState.Siege && !w.Factions[b.Faction].Fauna).ToList();
        var q = new Queue<int>();
        foreach (var p in w.Places)
        {
            if (p.Kind != PlaceKind.Base || p.Owner < 0 || !members(p.Owner) || besieged.Contains(p.Id)) continue;
            ok[p.Id] = true;
            q.Enqueue(p.Id);
        }

        if (q.Count == 0)
        {
            Array.Fill(ok, true);                                            // no base: no supply line to cut (bandit camps, nomads)
            return ok;
        }

        while (q.TryDequeue(out int u))
        {
            foreach (var (v, r) in At(u))
            {
                if (v >= ok.Length || ok[v]) continue;
                var pv = w.Places[v];
                if (BlocksPlace(w, faction, pv, besieged, rel))
                {
                    if (pv.Owner >= 0 && members(pv.Owner)) { } else { BlockedByPlace++; continue; }
                }

                if (BlocksRoad(w, faction, _roads[r], camped, rel)) { BlockedByBand++; continue; }
                ok[v] = true;
                q.Enqueue(v);
            }
        }

        foreach (var p in w.Places)
        {
            if (At(p.Id).Count == 0) { ok[p.Id] = true; continue; }        // not on the road graph: no road to cut
            if (p.Owner >= 0 && members(p.Owner) && !ok[p.Id] && p.Kind != PlaceKind.Lair) CutOff++;
        }

        return ok;
    }

    /// <summary>
    /// The places that cut a faction's cut-off places from its supplied ones: hostile-held or besieged places that touch both sides, so
    /// taking one of them opens the road again (the lifeline motive).
    /// </summary>
    public HashSet<int> Cutters(World w, int faction, bool[] supplied, IRelations rel, Func<int, bool>? members = null)
    {
        members ??= f => f == faction;
        var cutters = new HashSet<int>();
        // the cut-off side: the faction's unsupplied places and the friendly or empty places joined to them
        var side = new bool[w.Places.Count];
        var q = new Queue<int>();
        foreach (var p in w.Places)
            if (p.Owner >= 0 && members(p.Owner) && !supplied[p.Id] && p.Kind != PlaceKind.Lair) { side[p.Id] = true; q.Enqueue(p.Id); }
        if (q.Count == 0) return cutters;
        var besieged = new HashSet<int>(w.Bands.Where(b => b.Alive && b.State == BandState.Siege && b.Target >= 0
            && b.Faction != faction && w.Hostile(faction, b.Faction, rel)).Select(b => b.Target));
        while (q.TryDequeue(out int u))
        {
            foreach (var (v, _) in At(u))
            {
                if (v >= side.Length || side[v] || supplied[v]) continue;
                var pv = w.Places[v];
                if (BlocksPlace(w, faction, pv, besieged, rel) && !(pv.Owner >= 0 && members(pv.Owner))) continue;
                side[v] = true;
                q.Enqueue(v);
            }
        }

        foreach (var p in w.Places)
        {
            if (!BlocksPlace(w, faction, p, besieged, rel) || (p.Owner >= 0 && members(p.Owner))) continue;
            bool toSupplied = false, toSide = false;
            foreach (var (v, _) in At(p.Id))
            {
                if (v >= side.Length) continue;
                toSupplied |= supplied[v];
                toSide |= side[v];
            }

            if (toSupplied && toSide && cutters.Add(p.Id)) CuttersFound++;
        }

        return cutters;
    }
}
