// Dustlife (A-Life for Kenshi) for Meitou. Author: PZA. Licence: GPLv3 or later.
using Dustlife.Core;
using Dustlife.Game;

namespace Dustlife;

/// <summary>
/// Dustlife for other Meitou mods (version 1), in the style of The Wasteland Remembers' <c>WastelandApi</c>: a public static class read by
/// reflection (<c>Type.GetType("Dustlife.DustlifeApi, Dustlife")</c>), so no mod depends on Dustlife. Plain types only (strings, numbers,
/// bools, lists of string-keyed dictionaries); nothing throws; before a war runs, reads come back empty. Read it from your own system's
/// serial step (<c>Inputs</c>, <c>Schedule</c>, <c>SlowWorld</c>): the war changes only in Dustlife's <c>SlowWorld</c>. <see cref="Version"/>
/// only grows; nothing is removed or changes meaning. Version 2: <see cref="Generation"/> and the <c>Node</c> fields; the API is cleared
/// for every new world, so a world with Dustlife off reads as inactive (a reader saw the last world's war before: TWR's probe, 2026-11-20).
/// </summary>
public static class DustlifeApi
{
    public const int Version = 2;

    /// <summary>Dustlife's squads in the game carry a <c>Platoon.ModTag</c> starting with this (a rival picker can skip them).</summary>
    public const string SquadTagPrefix = Squads.TagPrefix;

    static Core.World? s_war;
    static long s_generation;

    internal static void Attach(Core.World? war) => s_war = war;

    /// <summary>
    /// A new game world: the last world's war is gone from the API until this one's starts. Returns the new world's generation.
    /// </summary>
    internal static long NewWorld()
    {
        s_war = null;
        return ++s_generation;
    }

    /// <summary>
    /// Which game world the answers are about: it changes with every new game and load. A news reader keeps (Generation, last Seq) and starts
    /// from Seq 0 when the generation changes (a new world's news counts from 1 again).
    /// </summary>
    public static long Generation => s_generation;

    /// <summary>Whether Dustlife runs the war in this world (a mod that tells war stories of its own can step aside).</summary>
    public static bool Active => s_war != null;

    /// <summary>Whether a squad's tag is one of Dustlife's.</summary>
    public static bool IsDustlifeSquad(string? tag) => tag != null && tag.StartsWith(SquadTagPrefix, StringComparison.Ordinal);

    /// <summary>
    /// The war's news after <paramref name="sinceSeq"/>, oldest first (the last 300 at most): <c>Seq</c> (long), <c>Hours</c> (game hours),
    /// <c>Key</c> (the sentence's key: "took_food", "raid", "march", "town_changed"...), <c>Text</c> (English), <c>Importance</c> (0 to 3),
    /// <c>Faction</c> and <c>Other</c> (faction keys or ""), <c>Town</c> (the place's placement id or ""), <c>Place</c> (its name), <c>X</c>,
    /// <c>Z</c>. Keep the last <c>Seq</c> you saw and ask from it.
    /// </summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, object>> News(long sinceSeq = 0)
    {
        if (s_war is not { } w) return [];
        try
        {
            return [.. w.Events.Where(e => e.Seq > sinceSeq).Select(e => (IReadOnlyDictionary<string, object>)Row(w, e))];
        }
        catch (Exception)
        {
            return [];
        }
    }

    static Dictionary<string, object> Row(Core.World w, WorldEvent e)
    {
        var p = w.PlaceById(e.Place);
        return new()
        {
            ["Seq"] = e.Seq, ["Generation"] = s_generation, ["Hours"] = e.Time, ["Key"] = e.Key, ["Text"] = w.Say(e), ["Importance"] = e.Importance,
            ["Faction"] = FactionKey(w, e.A), ["Other"] = FactionKey(w, e.B), ["Town"] = p?.Key ?? "", ["Node"] = NodeOf(p), ["Place"] = p?.Name ?? "",
            ["X"] = (double)e.Pos.X, ["Z"] = (double)e.Pos.Y,
        };
    }

    /// <summary>The place's node id as Logistics Core and TWR name it (<c>town:&lt;placement id&gt;</c>), or "".</summary>
    static string NodeOf(Place? p) => p == null ? "" : "town:" + p.Key;

    static string FactionKey(Core.World w, int f) => f >= 0 && f < w.Factions.Count ? w.Factions[f].Key : "";

    /// <summary>
    /// Every place in the war: <c>Town</c> (placement id), <c>Name</c>, <c>Kind</c> ("base", "resource", "territory", "lair"), <c>Holder</c>
    /// (faction key or ""), <c>Besieged</c> (bool), <c>X</c>, <c>Z</c>.
    /// </summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, object>> Places()
    {
        if (s_war is not { } w) return [];
        try
        {
            var besieged = w.BesiegedPlaces();
            return [.. w.Places.Select(p => (IReadOnlyDictionary<string, object>)new Dictionary<string, object>
            {
                ["Town"] = p.Key, ["Node"] = NodeOf(p), ["Name"] = p.Name, ["Kind"] = p.Kind.ToString().ToLowerInvariant(), ["Holder"] = FactionKey(w, p.Owner),
                ["Besieged"] = besieged.Contains(p.Id), ["X"] = (double)p.Pos.X, ["Z"] = (double)p.Pos.Y,
            })];
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>Who holds a town in the war, by node id (<c>town:&lt;id&gt;</c>), placement id or name: a faction key, or "" (nobody, or not a place).</summary>
    public static string Holder(string town)
    {
        if (s_war is not { } w || string.IsNullOrEmpty(town)) return "";
        if (town.StartsWith("town:", StringComparison.Ordinal)) town = town[5..];          // a Logistics Core / TWR node id
        var p = w.Places.Find(x => x.Key == town) ?? w.Places.Find(x => x.Name == town);
        return p == null ? "" : FactionKey(w, p.Owner);
    }
}
