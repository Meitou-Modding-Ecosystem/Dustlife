// Dustlife (A-Life for Kenshi) for Meitou. Author: PZA. Licence: GPLv3 or later.
using System.Numerics;
using Dustlife.Core;
using Dustlife.Game.Vendor;
using Dustlife.Modules;
using Meitou.Data.Gameplay;
using Meitou.Simulation.Population;

namespace Dustlife.Game;

/// <summary>
/// Reads the game's world for Dustlife (port step 5): the factions and placed towns of <see cref="PopulationData"/>, Logistics Core's
/// edges between them, the towns' current versions, and where the player's people are. Only reads.
/// </summary>
public static class GameSource
{
    /// <summary>
    /// The war built from the game's data with the lore set-up, and the game's relations as Dustlife's.
    /// </summary>
    public static (Core.World War, GameRelations Rel) Build(PopulationData data, DustlifeSettings settings)
    {
        var w = new Core.World();
        w.Cfg.Speed = Lore.GameSpeed;
        var factions = Factions(data);
        GameMap.Build(w, factions, Towns(data));
        var edges = VanillaEdges.Build(data).Select(e => (Key(e.From), Key(e.To), e.Cost, e.Kind == EdgeKind.Overland));
        w.Roads = GameMap.Roads(w, edges);
        w.AddModule(new Economy(Lore.Economy(settings.CutRoads)));
        w.AddModule(new Slavery());
        w.AddModule(new Fauna());
        w.AddModule(new Warfare(Lore.War(settings.Motives, settings.HubTheatre)));

        var relations = data.Relations;
        int[] gameIndex = [.. w.Factions.Select(f => relations.IndexOf(f.Key))];
        var rel = new GameRelations(gameIndex, relations.IsHostile, f => relations.PlayerFaction >= 0 && relations.IsHostile(f, relations.PlayerFaction));
        return (w, rel);
    }

    static string Key(string nodeId) => nodeId.StartsWith("town:", StringComparison.Ordinal) ? nodeId[5..] : nodeId;

    static bool IsNest(TownSite s) => s.Record.Type is TownType.Nest or TownType.NestMarker;

    /// <summary>
    /// Who holds a town: its current version's faction; for a nest, which names none in the data, the faction of the squads that live there
    /// (its first resident squad's spawn faction, as the game spawns them: Wildlife when it names none).
    /// </summary>
    public static string? HolderOf(PopulationData data, TownSite s)
    {
        if (s.Town.Faction is { } f) return f;
        if (!IsNest(s)) return null;
        foreach (var link in s.Town.Residents)
        {
            if (data.Db.Find(link.Id) is not { } record) continue;
            int fi = data.SpawnFactionOf(SquadTemplate.From(record));
            if (fi >= 0 && fi < data.Factions.Count) return data.Factions[fi].Id;
        }

        return null;
    }

    /// <summary>
    /// The factions that hold a placed town, the player's left out. A faction whose towns are all nests is an animal faction.
    /// </summary>
    public static List<MapFaction> Factions(PopulationData data)
    {
        var holders = new Dictionary<string, (bool AllNests, int Count)>();
        foreach (var s in data.Sites)
        {
            if (HolderOf(data, s) is not { } f || f == FactionData.PlayerId) continue;
            bool nest = IsNest(s);
            holders[f] = holders.TryGetValue(f, out var h) ? (h.AllNests && nest, h.Count + 1) : (nest, 1);
        }

        var list = new List<MapFaction>();
        foreach (var fd in data.Factions)
        {
            if (!holders.TryGetValue(fd.Id, out var h)) continue;
            list.Add(new MapFaction(fd.Id, fd.Name, h.AllNests, Lore.Slavers.Contains(fd.Id)));
        }

        return list;
    }

    /// <summary>
    /// Every placed town with a placement id, by its base record's kind and its current holder.
    /// </summary>
    public static List<MapTown> Towns(PopulationData data) =>
        [.. data.Sites.Where(s => s.InstanceId.Length > 0)
            .Select(s => new MapTown(s.InstanceId, s.Record.Name, new Vector2(s.Position.X, s.Position.Z), (int)s.Record.Type, HolderOf(data, s)))];

    /// <summary>
    /// What each placed town is now: its override version and its holder.
    /// </summary>
    public static IEnumerable<(string Key, TownVersion Now)> Versions(PopulationData data) =>
        data.Sites.Where(s => s.InstanceId.Length > 0).Select(s => (s.InstanceId, new TownVersion(s.Replacement?.Id ?? "", HolderOf(data, s))));

    /// <summary>
    /// The game's site index of each Dustlife place (by placement id).
    /// </summary>
    public static Dictionary<int, int> SiteOfPlace(PopulationData data, Core.World w)
    {
        var map = new Dictionary<int, int>();
        foreach (var s in data.Sites)
            if (s.InstanceId.Length > 0 && w.FindPlace(s.InstanceId) is >= 0 and var id) map[id] = s.Index;
        return map;
    }

    /// <summary>
    /// Where the player's living characters stand (X/Z).
    /// </summary>
    public static IEnumerable<Vector2> PlayerPositions(Meitou.Simulation.World world, PopulationData data)
    {
        int player = data.Relations.PlayerFaction;
        var table = world.Characters;
        for (int s = 0; s < table.HighWater; s++)
        {
            if (!table.Next[s].Alive || table.Cold(s) is not { } c || c.Faction != player) continue;
            var p = table.Next[s].Position;
            yield return new Vector2(p.X, p.Z);
        }
    }
}
