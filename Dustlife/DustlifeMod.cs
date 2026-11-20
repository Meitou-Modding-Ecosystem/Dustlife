// Dustlife (A-Life for Kenshi) for Meitou. Author: PZA. Licence: GPLv3 or later.
using System.Reflection;
using Meitou.Modding;
using Meitou.Simulation;
using Meitou.Simulation.Population;

namespace Dustlife;

/// <summary>
/// The mod's entry, named by "entry" in meitou.json (RivalryCore#29): its settings and its world system (<see cref="DustlifeSystem"/>).
/// </summary>
public sealed class DustlifeMod : IMod
{
    /// <summary>
    /// The mod's manifest id.
    /// </summary>
    public const string Id = "pza.dustlife";

    /// <summary>
    /// The world system's name, unique within the mod; it keys the system's save data.
    /// </summary>
    public const string SystemName = "world";

    public void Initialize(ModContext context)
    {
        string version = typeof(DustlifeMod).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
        int plus = version.IndexOf('+');
        if (plus >= 0) version = version[..plus];
        context.Log($"v{version} - Dustlife (A-Life for Kenshi) for Meitou, by PZA: the war runs off screen, and its marching bands near you become the game's squads");
        var settings = new DustlifeSettings();
        context.AddSettings(settings);
        context.AddSystem(SystemName, b => new DustlifeSystem(b.Data, b.Find<PopulationSystem>(), settings, context.Log), SystemAnchor.End);
    }
}
