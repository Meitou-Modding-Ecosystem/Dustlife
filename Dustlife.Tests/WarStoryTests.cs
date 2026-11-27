// Dustlife (A-Life for Kenshi) for Meitou. Author: PZA. Licence: GPLv3 or later.
using System.Numerics;
using Dustlife.Core;

namespace Dustlife.Tests;

/// <summary>
/// The war story (F0): events grouped into arcs (a march, its siege, its end) and the story metrics, read from events only.
/// </summary>
public sealed class WarStoryTests
{
    sealed class Rel : IRelations
    {
        public bool Hostile(int a, int b) => a != b;
        public bool HostileToPlayer(int faction) => false;
    }

    static (World W, int Hn, int Sk, int Stack) War()
    {
        var w = new World();
        int hn = w.AddFaction("hn", "Holy Nation", false, false);
        int sk = w.AddFaction("sk", "Shek", false, false);
        int stack = w.AddPlace("stack", "Stack", Vector2.Zero, PlaceKind.Base, 3, hn);
        w.StartNew();
        return (w, hn, sk, stack);
    }

    /// <summary>A Shek band besieging Stack, far from anyone it could fight (so it stays for the test).</summary>
    static Band Besieger(World w, int sk, int stack)
    {
        var b = w.FindBand(w.SpawnBand(sk, stack, BandRole.Invader, new Vector2(1000, 0), "test"))!;
        (b.State, b.Target, b.StayUntil) = (BandState.Siege, stack, 1e9);
        return b;
    }

    static void At(World w, double hours)
    {
        var rel = new Rel();
        if (w.LastTick < 0) w.Advance(0, rel);
        for (double h = Math.Floor(w.Now) + 1; h <= hours; h++) w.Advance(h, rel);   // hour by hour: one call catches up a capped amount only
    }

    [Fact]
    public void A_march_a_siege_and_a_fall_make_one_arc()
    {
        var (w, hn, sk, stack) = War();
        var story = new WarStory();
        Besieger(w, sk, stack);
        w.Emit(EventType.Invasion, sk, hn, stack, Vector2.Zero, 3, "march", "Shek", "Stack", "Squin", "6", "Ashrikus");
        story.Read(w);
        At(w, 30);
        w.Emit(EventType.SiegeBegan, sk, hn, stack, Vector2.Zero, 2, "siege_begin", "Shek", "Stack", "Holy Nation");
        story.Read(w);
        At(w, 30 + 5 * 24);
        w.Emit(EventType.Capture, sk, hn, stack, Vector2.Zero, 3, "took", "Shek", "Stack", "Holy Nation");
        story.Read(w);

        var arc = Assert.Single(story.Done);
        Assert.Equal(WarStory.Outcome.Fell, arc.Result);
        Assert.Equal("Ashrikus", arc.Leader);
        Assert.Equal(6, arc.Bands);
        Assert.Equal(5 * 24, arc.SiegeHours, 3);
        var m = story.Metrics();
        Assert.Equal(1, m.Sieges);
        Assert.Equal(5.0, m.SiegeDaysMedian, 3);
        Assert.Equal(1.0, m.FallShare, 3);
        Assert.Contains("marched on Stack from Squin, besieged it from day 2, and took it on day 7.", story.Render());
    }

    [Fact]
    public void A_siege_given_up_ends_the_arc_without_a_fall()
    {
        var (w, hn, sk, stack) = War();
        var story = new WarStory();
        var band = Besieger(w, sk, stack);
        w.Emit(EventType.Invasion, sk, hn, stack, Vector2.Zero, 3, "march", "Shek", "Stack", "Squin", "2", "Talshi");
        w.Emit(EventType.SiegeBegan, sk, hn, stack, Vector2.Zero, 2, "siege_begin", "Shek", "Stack", "Holy Nation");
        story.Read(w);
        At(w, 72);
        band.State = BandState.Retreat;                                                     // it gives up and walks home
        w.Emit(EventType.Rout, hn, sk, stack, Vector2.Zero, 1, "siege_end", "Talshi", "Stack");
        story.Read(w);
        var arc = Assert.Single(story.Done);
        Assert.Equal(WarStory.Outcome.GivenUp, arc.Result);
        Assert.Equal(0.0, story.Metrics().FallShare, 3);
    }

    [Fact]
    public void A_place_retaken_within_a_week_is_a_flip_back()
    {
        var (w, hn, sk, stack) = War();
        var story = new WarStory();
        w.Emit(EventType.Capture, sk, hn, stack, Vector2.Zero, 3, "took", "Shek", "Stack", "Holy Nation");
        story.Read(w);
        At(w, 3 * 24);
        w.Emit(EventType.Capture, hn, sk, stack, Vector2.Zero, 3, "took", "Holy Nation", "Stack", "Shek");
        story.Read(w);
        At(w, 20 * 24);
        w.Emit(EventType.Capture, sk, hn, stack, Vector2.Zero, 3, "took", "Shek", "Stack", "Holy Nation");   // 17 days later: not a flip
        story.Read(w);
        Assert.Equal(3, story.Captures);
        Assert.Equal(1, story.FlipBacks);
    }

    [Fact]
    public void An_arc_nobody_hears_of_fades_and_warlord_tenures_are_measured()
    {
        var (w, hn, sk, stack) = War();
        var story = new WarStory();
        w.Emit(EventType.Invasion, sk, hn, stack, Vector2.Zero, 3, "march", "Shek", "Stack", "Squin", "2", "Talshi");
        w.Emit(EventType.Warlord, sk, -1, -1, Vector2.Zero, 2, "warlord_new", "Talshi", "Shek");
        story.Read(w);
        At(w, WarStory.FadeHours + 10);
        w.Emit(EventType.Warlord, sk, -1, -1, Vector2.Zero, 2, "warlord_succ", "Talshi", "Kado", "Shek");
        story.Read(w);
        Assert.Equal(WarStory.Outcome.Faded, Assert.Single(story.Done).Result);
        Assert.Equal(WarStory.FadeHours + 10, Assert.Single(story.WarlordTenures), 3);
    }

    [Fact]
    public void A_siege_whose_besiegers_all_fell_was_broken()
    {
        var (w, hn, sk, stack) = War();
        var story = new WarStory();
        var band = Besieger(w, sk, stack);
        w.Emit(EventType.Invasion, sk, hn, stack, Vector2.Zero, 3, "march", "Shek", "Stack", "Squin", "1", "Kado");
        w.Emit(EventType.SiegeBegan, sk, hn, stack, Vector2.Zero, 2, "siege_begin", "Shek", "Stack", "Holy Nation");
        story.Read(w);
        w.Kill(band);
        story.Read(w);
        Assert.Equal(WarStory.Outcome.Broken, Assert.Single(story.Done).Result);
        Assert.Contains("were broken at its walls", story.Render());
    }
}
