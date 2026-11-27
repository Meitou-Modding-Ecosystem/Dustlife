// Dustlife (A-Life for Kenshi) for Meitou. Author: PZA. Licence: GPLv3 or later.
using System.Numerics;
using System.Reflection;
using Meitou.Simulation.Population;

namespace Dustlife.Game;

/// <summary>
/// <see cref="ISquadHost"/> on the engine's world hooks (docs/modding.md "World hooks", "Driving NPCs"): <c>SpawnPlatoon</c>, a
/// <c>ModTag</c>, <c>HoldPlatoon</c> and <c>SendPlatoon</c>. Serial phases only (Dustlife's system runs in <c>SlowWorld</c>).
/// </summary>
public sealed class EngineSquads(Meitou.Simulation.World world, PopulationSystem population, int[] gameFaction, IReadOnlyDictionary<int, int> siteOfPlace)
    : ISquadHost
{
    // SpawnPlatoon gained an optional maxMembers parameter in Meitou 0.22.2-nightly.20261124.3 (binary-incompatible with 0.21 / 0.22.1, and
    // the released 0.23 has only the new form): resolved by name and parameter shape, filling any trailing optional parameters with their
    // defaults, so one build runs on both.
    static readonly MethodInfo? SpawnPlatoonMethod = typeof(PopulationSystem)
        .GetMethods(BindingFlags.Public | BindingFlags.Instance)
        .FirstOrDefault(m =>
        {
            if (m.Name != "SpawnPlatoon" || m.ReturnType != typeof(int)) return false;
            var ps = m.GetParameters();
            return ps.Length >= 8
                && ps[0].ParameterType == typeof(Meitou.Simulation.World) && ps[1].ParameterType == typeof(int)
                && ps[2].ParameterType == typeof(string) && ps[3].ParameterType == typeof(int)
                && ps[4].ParameterType == typeof(Vector2) && ps[5].ParameterType == typeof(float)
                && ps[6].ParameterType == typeof(float) && ps[7].ParameterType == typeof(long)
                && ps.Skip(8).All(p => p.HasDefaultValue);
        });

    int CallSpawnPlatoon(int faction, string template, int site, Vector2 at, float minRadius, float maxRadius, long money = 0)
    {
        if (SpawnPlatoonMethod is null) return -1;
        var ps = SpawnPlatoonMethod.GetParameters();
        var args = new object?[ps.Length];
        args[0] = world; args[1] = faction; args[2] = template; args[3] = site;
        args[4] = at; args[5] = minRadius; args[6] = maxRadius; args[7] = money;
        for (int i = 8; i < ps.Length; i++) args[i] = ps[i].DefaultValue;
        return SpawnPlatoonMethod.Invoke(population, args) is int id ? id : -1;
    }

    /// <summary>A squad is placed this far at most from the band (a ring round it, never outside).</summary>
    public const float SpawnRing = 600;

    /// <summary>Why the last spawn was refused, for the log ("" when it wasn't).</summary>
    public string LastRefusal { get; private set; } = "";

    public int Spawn(int faction, string template, int home, Vector2 at, Vector2 goal, string tag)
    {
        if (faction < 0 || faction >= gameFaction.Length || gameFaction[faction] < 0) return Refuse($"faction {faction} has no game faction");
        int site = siteOfPlace.TryGetValue(home, out int s) ? s : siteOfPlace.Values.DefaultIfEmpty(-1).First();
        if (site < 0) return Refuse($"no game town for place {home}");
        int id = CallSpawnPlatoon(gameFaction[faction], template, site, at, 0, SpawnRing);
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
