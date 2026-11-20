// Dustlife (A-Life for Kenshi) for Meitou. Author: PZA. Licence: GPLv3 or later.
using System.Numerics;
using Dustlife.Core;

namespace Dustlife.Game;

/// <summary>A faction as the game has it: its record id, name, and the Dustlife flags.</summary>
public sealed record MapFaction(string Key, string Name, bool Fauna, bool Slaver, bool Disabled = false, float? Aggression = null);

/// <summary>
/// A placed town as the game has it: its placement id, name, X/Z, the TOWN type number (the game's <c>TownType</c>), its current holder's
/// faction key (null: nobody), and whether something outside the simulation holds it (a boss hideout).
/// </summary>
public sealed record MapTown(string Key, string Name, Vector2 Pos, int Type, string? Faction, bool Locked = false);

/// <summary>
/// Builds a Dustlife world from the game's factions and towns (port step 5), by the reference bridge's rules, for the mod and the balance
/// runner alike: which towns are places, of which kind and worth, and who holds them. Roads come separately (Logistics Core's edges).
/// </summary>
public static class GameMap
{
    /// <summary>
    /// The reference bridge's place rule: the game's town type to a place kind and resource points. A nest held by people is territory;
    /// a nest held by nobody is left out (null); Drin (a partly rebuilt barracks, the UC's biggest post in Bast) is the UC's base there.
    /// </summary>
    public static (PlaceKind Kind, int Res)? KindOf(MapTown t, bool ownerIsFauna, bool hasOwner)
    {
        var (kind, res) = t.Type switch
        {
            2 => (PlaceKind.Base, 3),
            6 => (PlaceKind.Base, 2),
            3 or 5 => (PlaceKind.Resource, 2),
            7 or 1 => (PlaceKind.Resource, 1),
            4 => (PlaceKind.Territory, 1),
            9 => (PlaceKind.Territory, 0),
            _ => (PlaceKind.Lair, 0),
        };
        if (kind == PlaceKind.Lair && hasOwner && !ownerIsFauna) (kind, res) = (PlaceKind.Territory, 1);
        if (kind == PlaceKind.Lair && !hasOwner) return null;
        if (t.Name == "Drin") (kind, res) = (PlaceKind.Base, 1);
        return (kind, res);
    }

    /// <summary>
    /// Adds the factions and places to <paramref name="w"/> (empty, not started) and returns, for each town, its place index (-1: not a
    /// place).
    /// </summary>
    public static int[] Build(World w, IReadOnlyList<MapFaction> factions, IReadOnlyList<MapTown> towns)
    {
        foreach (var f in factions)
        {
            int idx = w.AddFaction(f.Key, f.Name, f.Fauna, f.Slaver);
            if (f.Aggression is { } a) w.Factions[idx].Aggression = a;
            if (f.Disabled) w.Factions[idx].Enabled = false;
        }

        var placeOf = new int[towns.Count];
        for (int i = 0; i < towns.Count; i++)
        {
            var t = towns[i];
            int owner = t.Faction is null ? -1 : w.FindFaction(t.Faction);
            placeOf[i] = -1;
            if (KindOf(t, owner >= 0 && w.Factions[owner].Fauna, owner >= 0) is not var (kind, res)) continue;
            int id = w.AddPlace(t.Key, t.Name, t.Pos, kind, res, owner);
            if (t.Locked) w.Places[id].Locked = true;
            placeOf[i] = id;
        }

        return placeOf;
    }

    /// <summary>
    /// The roads between places from edges named by town placement ids (Logistics Core's <c>town:&lt;id&gt;</c> without the prefix); an
    /// edge to a town that is not a place is left out.
    /// </summary>
    public static Roads Roads(World w, IEnumerable<(string From, string To, float Cost, bool Overland)> edges)
    {
        var roads = new Roads();
        foreach (var (from, to, cost, overland) in edges)
        {
            int a = w.FindPlace(from), b = w.FindPlace(to);
            if (a >= 0 && b >= 0) roads.Add(a, b, cost, overland);
        }

        return roads;
    }
}

/// <summary>
/// Hostility by the game's own relations: Dustlife's faction indices mapped to the game's, asked through the given lookups (so a war the
/// player starts or ends reaches Dustlife at once).
/// </summary>
public sealed class GameRelations(int[] gameIndex, Func<int, int, bool> hostile, Func<int, bool> hostileToPlayer) : IRelations
{
    /// <summary>The game's faction index of each Dustlife faction (-1: none).</summary>
    public int[] GameIndex => gameIndex;

    public bool Hostile(int a, int b) =>
        a >= 0 && b >= 0 && a < gameIndex.Length && b < gameIndex.Length && gameIndex[a] >= 0 && gameIndex[b] >= 0 && hostile(gameIndex[a], gameIndex[b]);

    public bool HostileToPlayer(int faction) => faction >= 0 && faction < gameIndex.Length && gameIndex[faction] >= 0 && hostileToPlayer(gameIndex[faction]);
}
