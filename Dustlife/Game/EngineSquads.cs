// Dustlife (A-Life for Kenshi) for Meitou. Author: PZA. Licence: GPLv3 or later.
using System.Numerics;
using Meitou.Simulation.Population;

namespace Dustlife.Game;

/// <summary>
/// <see cref="ISquadHost"/> on the engine's world hooks (docs/modding.md "World hooks", "Driving NPCs"): <c>SpawnPlatoon</c>, a
/// <c>ModTag</c>, <c>HoldPlatoon</c> and <c>SendPlatoon</c>. Serial phases only (Dustlife's system runs in <c>SlowWorld</c>).
/// </summary>
public sealed class EngineSquads(Meitou.Simulation.World world, PopulationSystem population, int[] gameFaction, IReadOnlyDictionary<int, int> siteOfPlace)
    : ISquadHost
{
    /// <summary>A squad is placed this far at most from the band (a ring round it, never outside).</summary>
    public const float SpawnRing = 600;

    /// <summary>Why the last spawn was refused, for the log ("" when it wasn't).</summary>
    public string LastRefusal { get; private set; } = "";

    public int Spawn(int faction, string template, int home, Vector2 at, Vector2 goal, string tag)
    {
        if (faction < 0 || faction >= gameFaction.Length || gameFaction[faction] < 0) return Refuse($"faction {faction} has no game faction");
        int site = siteOfPlace.TryGetValue(home, out int s) ? s : siteOfPlace.Values.DefaultIfEmpty(-1).First();
        if (site < 0) return Refuse($"no game town for place {home}");
        int id = population.SpawnPlatoon(world, gameFaction[faction], template, site, at, 0, SpawnRing);
        if (id < 0) return Refuse($"the game would not spawn {template} at town {site}");
        if (world.Platoons.Find(id) is not { } platoon) return Refuse($"squad {id} not found after its spawn");
        LastRefusal = "";
        platoon.ModTag = tag;
        PopulationSystem.HoldPlatoon(world, id, true);
        PopulationSystem.SendPlatoon(world, id, goal);
        return id;
    }

    int Refuse(string why)
    {
        LastRefusal = why;
        return -1;
    }

    public void Send(int squad, Vector2 goal) => PopulationSystem.SendPlatoon(world, squad, goal);

    public (bool Dead, int Size, Vector2 Pos)? Look(int squad) =>
        world.Platoons.Find(squad) is { } p ? (p.Dead, p.Size, p.Position) : null;

    public IEnumerable<(int Squad, string Tag)> Tagged() =>
        world.Platoons.All.Where(p => p.ModTag.StartsWith(Squads.TagPrefix, StringComparison.Ordinal)).Select(p => (p.Id, p.ModTag));
}
