// Dustlife (A-Life for Kenshi) for Meitou. Author: PZA. Licence: GPLv3 or later.
using System.Numerics;
using Dustlife.Core;

namespace Dustlife.Modules;

/// <summary>
/// Animals (Warfare's monster rules), ported from the reference: nests fill up to a per-region cap, animals wander between nearby places
/// and attack people, and never hold anything. Animals never take more than their share of the world's band cap.
/// </summary>
public sealed class Fauna : IModule, ISavedModule
{
    public int PerRegion = 5;
    public float RegionRadius = 60000;
    /// <summary>Animals take at most this share of the world's band cap.</summary>
    public float MaxShare = 0.15f;
    /// <summary>The next spawn hour, by nest key.</summary>
    public Dictionary<string, double> NextSpawn { get; } = [];

    public string Name => "fauna";

    public void Save(BinaryWriter w)
    {
        w.Write(NextSpawn.Count);
        foreach (var (k, v) in NextSpawn.OrderBy(x => x.Key, StringComparer.Ordinal)) { w.Write(k); w.Write(v); }
    }

    public Action Load(BinaryReader r, SaveMap map)
    {
        int n = WorldSave.Count(r);
        var rows = new List<(string, double)>();
        for (int i = 0; i < n; i++) rows.Add((r.ReadString(), WorldSave.FinD(r)));
        return () =>
        {
            NextSpawn.Clear();
            foreach (var (k, v) in rows) NextSpawn[k] = v;
        };
    }

    static bool Nest(World w, Place p) =>
        p.Kind == PlaceKind.Lair && p.Owner >= 0 && w.Factions[p.Owner].Fauna && w.Factions[p.Owner].Enabled && p.Active && !p.Locked;

    public void OnNewWorld(World w)
    {
        foreach (var p in w.Places) if (Nest(w, p)) w.SpawnBand(p.Owner, p.Id, BandRole.Fauna, p.Pos, Name);
    }

    public void Tick(World w, float dt, IRelations rel)
    {
        int mine = w.Bands.Count(b => b.Alive && b.Owner == Name);
        int share = Math.Max(1, (int)(w.Cfg.MaxBands * MaxShare));
        foreach (var p in w.Places)
        {
            if (!Nest(w, p)) continue;
            double next = NextSpawn.GetValueOrDefault(p.Key);
            if (next == 0) NextSpawn[p.Key] = next = w.Now + w.Rng.Range(1.0f, 12.0f);
            if (w.Now < next) continue;
            NextSpawn[p.Key] = w.Now + w.Rng.Range(4.0f, 12.0f) / Math.Max(0.05f, p.Activity);
            if (mine >= share || w.NearestPlayerDist(p.Pos) < w.Cfg.NoSpawnRadius) continue;   // never in front of the player
            int region = 0, home = 0;
            foreach (var b in w.Bands)
            {
                if (!b.Alive || b.Owner != Name) continue;
                if (Vector2.Distance(b.Pos, p.Pos) < RegionRadius) region++;
                if (b.Home == p.Id) home++;
            }

            if (region < PerRegion && home < p.Cap && w.SpawnBand(p.Owner, p.Id, BandRole.Fauna, p.Pos, Name) > 0) mine++;
        }
    }

    /// <summary>
    /// 90% wander within the region, 10% further afield; never into a city, a frozen place or a hideout.
    /// </summary>
    public bool Think(World w, Band b, IRelations rel)
    {
        if (b.Owner != Name) return false;
        if (b.State != BandState.Settled || w.Now < b.StayUntil) return true;
        float range = w.Rng.Chance(0.9f) ? RegionRadius : RegionRadius * 2.0f;
        var c = w.Places.Where(o => o.Kind != PlaceKind.Base && o.Active && !o.Locked && Vector2.Distance(b.Pos, o.Pos) < range).Select(o => o.Id).ToList();
        if (c.Count == 0) { b.StayUntil = w.Now + 12; return true; }
        w.Send(b, c[w.Rng.Int(0, c.Count - 1)], BandRole.Fauna);
        return true;
    }

    public bool OnArrive(World w, Band b, IRelations rel)
    {
        if (b.Owner != Name) return false;
        b.State = BandState.Settled;
        b.StayUntil = w.Now + w.Rng.Range(2.0f, 12.0f);
        return true;
    }
}
