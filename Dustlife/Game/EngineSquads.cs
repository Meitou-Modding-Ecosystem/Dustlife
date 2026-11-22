// Dustlife (A-Life for Kenshi) for Meitou. Author: PZA. Licence: GPLv3 or later.
using System.Numerics;
using System.Reflection;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Population;

namespace Dustlife.Game;

/// <summary>
/// <see cref="ISquadHost"/> on the engine's world hooks (docs/modding.md "World hooks", "Driving NPCs"): <c>SpawnPlatoon</c>, a
/// <c>ModTag</c>, <c>HoldPlatoon</c> and <c>SendPlatoon</c>, and where the game has it (VlcekM/MeitouClient#1173, not in 0.22.1)
/// <c>DespawnPlatoon</c>, found by reflection so the mod still loads on every Meitou from 0.20.1. Serial phases only (Dustlife's system runs
/// in <c>SlowWorld</c>).
/// </summary>
public sealed class EngineSquads(Meitou.Simulation.World world, PopulationSystem population, int[] gameFaction, IReadOnlyDictionary<int, int> siteOfPlace)
    : ISquadHost
{
    /// <summary>A squad is placed this far at most from the band (a ring round it, never outside).</summary>
    public const float SpawnRing = 600;

    /// <summary>Why the last spawn was refused, for the log ("" when it wasn't).</summary>
    public string LastRefusal { get; private set; } = "";

    static readonly MethodInfo? Despawn = typeof(PopulationSystem).GetMethod(
        "DespawnPlatoon", BindingFlags.Public | BindingFlags.Instance, [typeof(Meitou.Simulation.World), typeof(int)]);

    public bool CanGiveBack => Despawn?.ReturnType == typeof(bool);

    public bool GiveBack(int squad)
    {
        try
        {
            return CanGiveBack && Despawn!.Invoke(population, [world, squad]) is true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public bool Loaded(int squad) => world.Platoons.Find(squad) is not { State: PlatoonState.Unloaded };

    /// <summary>The running game's code-mod API (the constant as the game was built, read at run time; 2 when it can't be read).</summary>
    public static readonly int GameApi = ReadGameApi();

    static int ReadGameApi()
    {
        try
        {
            return typeof(Meitou.Modding.ModApi).GetField("Version")?.GetValue(null) is int v ? v : 2;
        }
        catch (Exception)
        {
            return 2;
        }
    }

    public bool HoldStopsLoaded => GameApi < 3;

    public void SetHeld(int squad, bool held) => PopulationSystem.HoldPlatoon(world, squad, held);

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
