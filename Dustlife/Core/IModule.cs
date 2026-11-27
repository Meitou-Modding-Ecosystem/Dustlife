// Dustlife (A-Life for Kenshi) for Meitou. Author: PZA. Licence: GPLv3 or later.
namespace Dustlife.Core;

/// <summary>
/// A gameplay module on top of the core (Warfare, Fauna, ...). Every hook is optional; a hook that returns true has handled the band.
/// </summary>
public interface IModule
{
    /// <summary>Unique; also the band owner tag and the save tag.</summary>
    string Name { get; }

    void OnNewWorld(World world) { }

    void Tick(World world, float dt, IRelations rel) { }

    /// <summary>A settled or besieging band's turn to think.</summary>
    bool Think(World world, Band band, IRelations rel) => false;

    /// <summary>A moving band reached its target position.</summary>
    bool OnArrive(World world, Band band, IRelations rel) => false;

    void OnFightEnd(World world, Band? winner, Band? loser, IRelations rel) { }

    /// <summary>True: these two bands don't fight this tick, though in reach (a siege's investment: besiegers and the defenders wait).</summary>
    bool HoldsFire(World world, Band a, Band b) => false;

    void PostTick(World world, float dt, IRelations rel) { }
}
