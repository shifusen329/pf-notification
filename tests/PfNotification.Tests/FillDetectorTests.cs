using PfNotification.Detection;

namespace PfNotification.Tests;

public sealed class FillDetectorTests
{
    private const int Full = 8;
    private const long Tick = 250; // matches the plugin's poll interval

    [Fact]
    public void HostingUntilFullFiresOnce()
    {
        var h = Harness.Settled(1).Run(4, 5_000).Run(7, 5_000).Run(8, 30_000);

        Assert.Equal([FillDecision.Fire], h.Decisions);
        Assert.Equal(FillState.Latched, h.State);
    }

    [Fact]
    public void FireWaitsForTheCountToHold()
    {
        var h = Harness.Settled(7).Run(8, FillDetector.StableMs - Tick);

        Assert.Empty(h.Decisions);
        Assert.Equal(FillState.Confirming, h.State);

        h.Run(8, 2 * Tick);
        Assert.Equal([FillDecision.Fire], h.Decisions);
    }

    [Fact]
    public void FlickerShorterThanStableDoesNotFire()
    {
        var h = Harness.Settled(7).Run(8, 1_000).Run(7, 5_000).Run(8, 1_000).Run(7, 5_000);

        Assert.Empty(h.Decisions);
        Assert.Equal(FillState.Armed, h.State);
    }

    [Fact]
    public void AlreadyFullWhenLoadedLatchesWithoutFiring()
    {
        var h = Harness.Settled(8).Run(8, 30_000);

        Assert.Empty(h.Decisions);
        Assert.Equal(FillState.Latched, h.State);
    }

    [Fact]
    public void LateCrossRealmDataAfterLoginIsTreatedAsBaseline()
    {
        // Cross-realm data can arrive a few seconds after login: 0, then 8. That's not a fill.
        var h = new Harness().Run(0, 3_000).Run(8, 30_000);

        Assert.Empty(h.Decisions);
        Assert.Equal(FillState.Latched, h.State);
    }

    [Fact]
    public void SlotRefillAfterRearmFiresAgain()
    {
        var h = Harness.Settled(7)
            .Run(8, 3_000)
            .Run(7, FillDetector.RearmMs + 2_000)
            .Run(8, 3_000);

        Assert.Equal([FillDecision.Fire, FillDecision.Fire], h.Decisions);
    }

    [Fact]
    public void BriefDropBelowTargetDoesNotRearm()
    {
        var h = Harness.Settled(7).Run(8, 3_000).Run(7, 5_000).Run(8, 5_000);

        Assert.Equal([FillDecision.Fire], h.Decisions);
    }

    [Fact]
    public void DisbandThenNewPartyFiresAgain()
    {
        var h = Harness.Settled(7)
            .Run(8, 3_000)
            .Run(0, 15_000)
            .Run(3, 5_000)
            .Run(8, 3_000);

        Assert.Equal([FillDecision.Fire, FillDecision.Fire], h.Decisions);
    }

    [Fact]
    public void DutyFinderEntryDoesNotFire()
    {
        // The party list may fill a tick before BoundByDuty / BetweenAreas is set.
        var h = Harness.Settled(0)
            .Run(8, Tick)
            .Run(8, 60_000, frozen: true)
            .Run(0, 30_000);

        Assert.Empty(h.Decisions);
        Assert.Equal(FillState.Armed, h.State);
    }

    [Fact]
    public void LeavingDutyWithABriefZeroDoesNotFireAgain()
    {
        // After a duty the source flips back from the party list to the cross-realm proxy, which can read 0 for a moment.
        var h = Harness.Settled(7)
            .Run(8, 3_000)
            .Run(8, 60_000, frozen: true)
            .Run(0, FillDetector.FrozenTailMs + 3_000)
            .Run(8, 30_000);

        Assert.Equal([FillDecision.Fire], h.Decisions);
    }

    [Fact]
    public void FullWhileFrozenNeverFires()
    {
        var h = Harness.Settled(4).Run(8, 60_000, frozen: true).Run(8, 30_000);

        Assert.Empty(h.Decisions);
        Assert.Equal(FillState.Latched, h.State);
    }

    [Fact]
    public void JoiningAFullPartyIsSuppressed()
    {
        var h = Harness.Settled(0).Run(8, 5_000);

        Assert.Equal([FillDecision.SuppressedJoinedFull], h.Decisions);
        Assert.Equal(FillState.Latched, h.State);
    }

    [Fact]
    public void QuickJoinsBeforeASteadyCountStillFire()
    {
        // Members joining under StableMs apart don't update the steady count, but it was already above 1.
        var h = Harness.Settled(3).Run(5, 1_000).Run(6, 1_000).Run(7, 1_000).Run(8, 3_000);

        Assert.Equal([FillDecision.Fire], h.Decisions);
    }

    [Fact]
    public void TargetChangeResetsTheBaseline()
    {
        var h = Harness.Settled(4).Run(4, FillDetector.SettleMs + 2_000, target: 4);

        Assert.Empty(h.Decisions);
        Assert.Equal(FillState.Latched, h.State);

        h.Run(3, FillDetector.RearmMs + 2_000, target: 4).Run(4, 3_000, target: 4);
        Assert.Equal([FillDecision.Fire], h.Decisions);
    }

    [Fact]
    public void LightPartyTargetFires()
    {
        var h = Harness.Settled(1, target: 4).Run(3, 5_000, target: 4).Run(4, 3_000, target: 4);

        Assert.Equal([FillDecision.Fire], h.Decisions);
    }

    [Fact]
    public void LogoutResetsAndTheNextLoginStartsFromABaseline()
    {
        var h = Harness.Settled(7)
            .Run(0, 5_000, loggedIn: false)
            .Run(8, 30_000);

        Assert.Empty(h.Decisions);
        Assert.Equal(FillState.Latched, h.State);
    }

    [Fact]
    public void SettlingStateWhileLoggedOut()
    {
        var h = new Harness().Run(0, 30_000, loggedIn: false);

        Assert.Equal(FillState.Settling, h.State);
    }

    /// <summary>Feeds the detector one reading per tick and records every decision other than None.</summary>
    private sealed class Harness
    {
        private readonly FillDetector detector = new();
        private long now = 5_000_000;

        public List<FillDecision> Decisions { get; } = [];

        public FillState State => detector.State;

        public static Harness Settled(int count, int target = Full) =>
            new Harness().Run(count, FillDetector.SettleMs + 1_000, target: target);

        public Harness Run(int count, long durationMs, bool frozen = false, int target = Full, bool loggedIn = true)
        {
            for (long elapsed = 0; elapsed < durationMs; elapsed += Tick)
            {
                var decision = detector.Update(new FillInput(now, count, target, loggedIn, frozen));
                if (decision != FillDecision.None)
                {
                    Decisions.Add(decision);
                }

                now += Tick;
            }

            return this;
        }
    }
}
