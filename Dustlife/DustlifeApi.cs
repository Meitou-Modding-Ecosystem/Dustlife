// Dustlife (A-Life for Kenshi) for Meitou. Author: PZA. Licence: GPLv3 or later.
using Dustlife.Core;
using Dustlife.Game;

namespace Dustlife;

/// <summary>
/// Dustlife for other Meitou mods (version 1), in the style of The Wasteland Remembers' <c>WastelandApi</c>: a public static class read by
/// reflection (<c>Type.GetType("Dustlife.DustlifeApi, Dustlife")</c>), so no mod depends on Dustlife. Plain types only (strings, numbers,
/// bools, lists of string-keyed dictionaries); nothing throws; before a war runs, reads come back empty. Read it from your own system's
/// serial step (<c>Inputs</c>, <c>Schedule</c>, <c>SlowWorld</c>): the war changes only in Dustlife's <c>SlowWorld</c>. <see cref="Version"/>
/// only grows; nothing is removed or changes meaning.
/// </summary>
public static class DustlifeApi
{
    public const int Version = 1;

    /// <summary>Dustlife's squads in the game carry a <c>Platoon.ModTag</c> starting with this (a rival picker can skip them).</summary>
    public const string SquadTagPrefix = Squads.TagPrefix;

    static Core.World? s_war;

    internal static void Attach(Core.World? war) => s_war = war;

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
            ["Seq"] = e.Seq, ["Hours"] = e.Time, ["Key"] = e.Key, ["Text"] = w.Say(e), ["Importance"] = e.Importance,
            ["Faction"] = FactionKey(w, e.A), ["Other"] = FactionKey(w, e.B), ["Town"] = p?.Key ?? "", ["Place"] = p?.Name ?? "",
            ["X"] = (double)e.Pos.X, ["Z"] = (double)e.Pos.Y,
        };
    }

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
                ["Town"] = p.Key, ["Name"] = p.Name, ["Kind"] = p.Kind.ToString().ToLowerInvariant(), ["Holder"] = FactionKey(w, p.Owner),
                ["Besieged"] = besieged.Contains(p.Id), ["X"] = (double)p.Pos.X, ["Z"] = (double)p.Pos.Y,
            })];
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>Who holds a town in the war, by placement id or name: a faction key, or "" (nobody, or not a place).</summary>
    public static string Holder(string town)
    {
        if (s_war is not { } w || string.IsNullOrEmpty(town)) return "";
        var p = w.Places.Find(x => x.Key == town) ?? w.Places.Find(x => x.Name == town);
        return p == null ? "" : FactionKey(w, p.Owner);
    }
}
