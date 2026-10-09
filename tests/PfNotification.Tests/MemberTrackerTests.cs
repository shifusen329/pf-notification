using PfNotification.Detection;

namespace PfNotification.Tests;

public sealed class MemberTrackerTests
{
    private const long Tick = 250; // matches the plugin's poll interval

    private static readonly PartyMember Whm = new(101, 24);
    private static readonly PartyMember Drg = new(102, 22);
    private static readonly PartyMember Pld = new(103, 19);
    private static readonly PartyMember Sch = new(104, 28);

    [Fact]
    public void JoinIsReportedOnceItHolds()
    {
        var h = Harness.Settled(Whm).Run([Whm, Drg], MemberTracker.JoinStableMs - Tick);
        Assert.Empty(h.Changes);

        h.Run([Whm, Drg], 2 * Tick);
        var change = Assert.Single(h.Changes);
        Assert.Equal([Drg], change.Joined);
        Assert.Empty(change.Left);
    }

    [Fact]
    public void FlickeringJoinIsIgnored()
    {
        var h = Harness.Settled(Whm).Run([Whm, Drg], 1_000).Run([Whm], 10_000);

        Assert.Empty(h.Changes);
    }

    [Fact]
    public void LeaveIsReportedOnceTheMemberStaysGone()
    {
        var h = Harness.Settled(Whm, Drg).Run([Whm], MemberTracker.LeaveStableMs - Tick);
        Assert.Empty(h.Changes);

        h.Run([Whm], 2 * Tick);
        var change = Assert.Single(h.Changes);
        Assert.Equal([Drg], change.Left);
        Assert.Empty(change.Joined);
    }

    [Fact]
    public void BriefEmptyReadIsIgnored()
    {
        // The cross-realm data can read empty for a moment, e.g. right after a duty.
        var h = Harness.Settled(Whm, Drg, Pld).Run([], 3_000).Run([Whm, Drg, Pld], 10_000);

        Assert.Empty(h.Changes);
        Assert.Equal(3, h.Count);
    }

    [Fact]
    public void LastOtherMemberLeavingIsReported()
    {
        var h = Harness.Settled(Whm).Run([], 10_000);

        Assert.Equal([Whm], Assert.Single(h.Changes).Left);
    }

    [Fact]
    public void DisbandingOrLeavingThePartyYourselfIsSilent()
    {
        var h = Harness.Settled(Whm, Drg, Pld).Run([], 10_000);

        Assert.Empty(h.Changes);
        Assert.Equal(0, h.Count);
    }

    [Fact]
    public void JoiningSomeoneElsesPartyIsSilent()
    {
        var h = Harness.Settled().Run([Whm, Drg, Pld], 10_000);

        Assert.Empty(h.Changes);
        Assert.Equal(3, h.Count);
    }

    [Fact]
    public void JoiningAPartyThatAppearsOverTwoTicksIsSilent()
    {
        var h = Harness.Settled().Run([Whm], Tick).Run([Whm, Drg, Pld], 10_000);

        Assert.Empty(h.Changes);
        Assert.Equal(3, h.Count);
    }

    [Fact]
    public void FirstMemberJoiningYourListingIsReported()
    {
        var h = Harness.Settled().Run([Whm], 5_000);

        Assert.Equal([Whm], Assert.Single(h.Changes).Joined);
    }

    [Fact]
    public void MembersJoiningSecondsApartAreEachReported()
    {
        var h = Harness.Settled().Run([Whm], 3_000).Run([Whm, Drg], 3_000);

        Assert.Equal(2, h.Changes.Count);
        Assert.Equal([Whm], h.Changes[0].Joined);
        Assert.Equal([Drg], h.Changes[1].Joined);
    }

    [Fact]
    public void JoinAndLeaveAtOnceAreBothReported()
    {
        var h = Harness.Settled(Whm, Drg).Run([Whm, Sch], 10_000);

        Assert.Equal(2, h.Changes.Count);
        Assert.Equal([Sch], h.Changes[0].Joined);
        Assert.Equal([Drg], h.Changes[1].Left);
    }

    [Fact]
    public void NothingIsReportedInADuty()
    {
        var h = Harness.Settled(Whm)
            .Run([Whm, Drg, Pld, Sch], 60_000, frozen: true)
            .Run([Whm, Drg, Pld, Sch], 20_000);

        Assert.Empty(h.Changes);
        Assert.Equal(4, h.Count);
    }

    [Fact]
    public void ChangesRightAfterADutyAreAdoptedSilently()
    {
        var h = Harness.Settled(Whm, Drg)
            .Run([Whm, Drg], 30_000, frozen: true)
            .Run([Whm], MemberTracker.FrozenTailMs - 1_000)
            .Run([Whm], 20_000);

        Assert.Empty(h.Changes);
        Assert.Equal(1, h.Count);
    }

    [Fact]
    public void JobChangeIsNotAJoinOrLeave()
    {
        var h = Harness.Settled(Whm).Run([Whm with { ClassJobId = 33 }], 20_000);

        Assert.Empty(h.Changes);
    }

    [Fact]
    public void PartyPresentAtLoginIsTheBaseline()
    {
        var h = new Harness().Run([Whm], 3_000).Run([Whm, Drg], 20_000);

        Assert.Empty(h.Changes);
        Assert.Equal(2, h.Count);
    }

    [Fact]
    public void LogoutClearsAndTheNextLoginStartsFromABaseline()
    {
        var h = Harness.Settled(Whm)
            .Run([], 5_000, loggedIn: false)
            .Run([Whm, Drg], 30_000);

        Assert.Empty(h.Changes);
        Assert.Equal(2, h.Count);
    }

    [Fact]
    public void EntriesWithoutAContentIdAreIgnored()
    {
        var h = Harness.Settled(Whm).Run([Whm, new PartyMember(0, 1)], 10_000);

        Assert.Empty(h.Changes);
        Assert.Equal(1, h.Count);
    }

    /// <summary>Feeds the tracker one reading per tick and records every non-null result.</summary>
    private sealed class Harness
    {
        private readonly MemberTracker tracker = new();
        private long now = 5_000_000;

        public List<MemberChanges> Changes { get; } = [];

        public int Count => tracker.Count;

        public static Harness Settled(params PartyMember[] others) =>
            new Harness().Run(others, MemberTracker.SettleMs + 1_000);

        public Harness Run(PartyMember[] others, long durationMs, bool frozen = false, bool loggedIn = true)
        {
            for (long elapsed = 0; elapsed < durationMs; elapsed += Tick)
            {
                if (tracker.Update(new MemberInput(now, others, loggedIn, frozen)) is { } changes)
                {
                    Changes.Add(changes);
                }

                now += Tick;
            }

            return this;
        }
    }
}
