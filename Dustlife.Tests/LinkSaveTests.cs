// Dustlife (A-Life for Kenshi) for Meitou. Author: PZA. Licence: GPLv3 or later.
using System.Numerics;
using Dustlife.Core;
using Dustlife.Game;

namespace Dustlife.Tests;

/// <summary>The link sections after the war in a save: each read whole or not at all, and the log says what was kept.</summary>
public sealed class LinkSaveTests
{
    static World War()
    {
        var w = new World();
        w.AddFaction("hn", "Holy Nation", false, false);
        w.AddFaction("shek", "Shek Kingdom", false, false);
        w.AddPlace("1111-rebirth.mod", "Bad Teeth", new Vector2(0, 0), PlaceKind.Base, 0, 0);
        w.AddPlace("2222-rebirth.mod", "Holy Farm", new Vector2(5000, 0), PlaceKind.Resource, 50, 1);
        return w;
    }

    /// <summary>The overrun section: the link's own header, then one overrun at Holy Farm (old holder the Holy Nation), or a damaged count.</summary>
    static void Overruns(BinaryWriter bw, World w, bool damaged)
    {
        var head = new MemoryStream();
        new WastelandLink(() => null).Write(head, w);
        bw.Write(head.ToArray()[..8]);
        bw.Write(damaged ? -5 : 1);
        if (damaged) return;
        bw.Write("2222-rebirth.mod");
        bw.Write("hn");
        bw.Write(10.0);
    }

    /// <summary>The outlaw section: the link's own header, then one time in each of its three parts, or a damaged count.</summary>
    static void OutlawTimes(BinaryWriter bw, bool damaged)
    {
        var head = new MemoryStream();
        new OutlawLink(() => null).Write(head);
        bw.Write(head.ToArray()[..8]);
        for (int part = 0; part < 3; part++)
        {
            bw.Write(damaged ? -5 : 1);
            if (damaged) return;
            bw.Write("Dolan Pike");
            bw.Write(500.0);
        }
    }

    static MemoryStream Save(World w, bool overrunsDamaged, bool outlawsDamaged)
    {
        var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            Overruns(bw, w, overrunsDamaged);
            OutlawTimes(bw, outlawsDamaged);
        }
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public void A_good_save_keeps_both_sections()
    {
        var w = War();
        var (twr, outlaws) = (new WastelandLink(() => null), new OutlawLink(() => null));
        var kept = LinkSave.Read(Save(w, false, false), w, twr, outlaws);
        Assert.Equal(new LinkSave.Result(1, 3, null), kept);
        Assert.Equal(1, twr.Pending);
        Assert.Equal(3, outlaws.Times);
    }

    [Fact]
    public void A_damaged_outlaw_section_keeps_the_overruns_and_says_so()
    {
        var w = War();
        var (twr, outlaws) = (new WastelandLink(() => null), new OutlawLink(() => null));
        LinkSave.Read(Save(w, false, false), w, twr, outlaws);                                  // times from an earlier load

        var kept = LinkSave.Read(Save(w, false, true), w, twr, outlaws);
        Assert.Equal(1, kept.Overruns);
        Assert.Equal(1, twr.Pending);
        Assert.Equal(0, kept.OutlawTimes);
        Assert.Equal(0, outlaws.Times);
        Assert.Contains("outlaw strike times could not be read", kept.Damage);
        Assert.Contains("1 overruns kept", kept.Damage);
    }

    [Fact]
    public void A_damaged_overrun_section_keeps_neither()
    {
        var w = War();
        var (twr, outlaws) = (new WastelandLink(() => null), new OutlawLink(() => null));
        LinkSave.Read(Save(w, false, false), w, twr, outlaws);                                  // state from an earlier load

        var kept = LinkSave.Read(Save(w, true, false), w, twr, outlaws);
        Assert.Equal(0, kept.Overruns);
        Assert.Equal(0, kept.OutlawTimes);
        Assert.Equal(0, twr.Pending);
        Assert.Equal(0, outlaws.Times);                                                          // the earlier load's times are gone too
        Assert.Contains("none kept, nor the outlaw strike times", kept.Damage);
    }
}
