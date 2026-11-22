// Dustlife (A-Life for Kenshi) for Meitou. Author: PZA. Licence: GPLv3 or later.
extern alias rivalry;

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Dustlife.Game;

namespace Dustlife.Tests;

/// <summary>
/// RivalryLink against the real Rivalry Core named in meitou.lock.json: every member it looks up by reflection exists there with the signature
/// it calls, so a Rivalry Core release that renames or reshapes one fails here instead of silently switching the link off in play.
/// </summary>
public sealed class RivalryContractTests
{
    /// <summary>
    /// This repo stamps the engine as the oldest Meitou Dustlife supports (Directory.Build.rsp, 0.21.0.0) while Rivalry Core 0.6.4+ references
    /// it as 0.22.0.0, and .NET never binds a reference to an older assembly. The game has one engine (0.22.x, which both bind to), so the
    /// tests hand Rivalry Core the engine assembly already loaded under its simple name.
    /// </summary>
    [ModuleInitializer]
    internal static void BindEngineByName() => AssemblyLoadContext.Default.Resolving += LoadedByName;

    static Assembly? LoadedByName(AssemblyLoadContext context, AssemblyName name)
    {
        if (name.Version is null || !name.Name!.StartsWith("Meitou", StringComparison.Ordinal)) return null;

        return context.LoadFromAssemblyName(new AssemblyName(name.Name));
    }

    [Fact]
    public void Every_member_RivalryLink_calls_is_in_the_locked_Rivalry_Core()
    {
        var mod = new rivalry::Rivalry.RivalryMod();
        var link = new RivalryLink(mod, f => f, _ => true);

        Assert.Empty(link.Find());
        Assert.True(link.Linked);
        Assert.True(link.HasQueue);
    }

    [Fact]
    public void A_band_is_proposed_with_Rivalry_Cores_hunt_threshold_as_its_grudge()
    {
        var mod = new rivalry::Rivalry.RivalryMod();
        mod.Options.HuntThreshold = 75;
        var link = new RivalryLink(mod, f => f, _ => true);

        link.Find();

        Assert.Equal(75, link.Grudge);
    }
}
