namespace PfNotification.Detection;

/// <summary>One observation of the party, taken on the framework thread.</summary>
/// <param name="NowMs">Monotonic milliseconds (<c>Environment.TickCount64</c>).</param>
/// <param name="Count">Party size including the local player; 0 when solo.</param>
/// <param name="Target">Size that counts as full.</param>
/// <param name="LoggedIn">False on the title screen or while the local player isn't loaded.</param>
/// <param name="Frozen">In a duty or between areas: party data is in flux and must not trigger anything.</param>
public readonly record struct FillInput(long NowMs, int Count, int Target, bool LoggedIn, bool Frozen);

public enum FillDecision
{
    /// <summary>Nothing to do.</summary>
    None,

    /// <summary>The party just filled: send the alert.</summary>
    Fire,

    /// <summary>The party became full by the local player joining it as the last member; they're at the PC.</summary>
    SuppressedJoinedFull,
}

public enum FillState
{
    /// <summary>Logged out, or waiting for party data to settle after login, load or a target change.</summary>
    Settling,

    /// <summary>In a duty or zoning (plus a short tail): changes are ignored.</summary>
    Frozen,

    /// <summary>Below target, ready to fire when the party fills.</summary>
    Armed,

    /// <summary>Full, waiting for the count to hold before firing.</summary>
    Confirming,

    /// <summary>Already alerted (or full when first seen); re-arms once the party stays below target.</summary>
    Latched,
}

/// <summary>
/// Decides when "the party filled" happened. Pure state machine with no game types, so it can be unit-tested.
/// Rules:
/// <list type="number">
/// <item>After login, load or a target change, wait <see cref="SettleMs"/>; the first reading after that is a baseline,
/// and a party that is already full latches without firing.</item>
/// <item>Fire when the count reaches the target and holds for <see cref="StableMs"/> while not frozen.</item>
/// <item>Frozen (duty, zoning, plus <see cref="FrozenTailMs"/> after) cancels a pending fire and latches a full party,
/// so entering or leaving a duty never fires.</item>
/// <item>If the last steady count before the fill was 1 or less, the player joined a full party: latch, don't fire.</item>
/// <item>Re-arm after the count stays below target for <see cref="RearmMs"/> while not frozen.</item>
/// </list>
/// </summary>
public sealed class FillDetector
{
    public const long StableMs = 2_000;
    public const long SettleMs = 10_000;
    public const long FrozenTailMs = 5_000;
    public const long RearmMs = 10_000;

    private bool hasBaseline;
    private long settleUntil = long.MinValue;
    private long frozenUntil = long.MinValue;
    private int target = -1;
    private bool latched;
    private long fullSince = -1;
    private long belowSince = -1;
    private int candidateCount = -1;
    private long candidateSince;
    private int steadyCount;

    public FillState State { get; private set; } = FillState.Settling;

    public FillDecision Update(in FillInput input)
    {
        var now = input.NowMs;

        if (!input.LoggedIn || (input.Target != target))
        {
            target = input.Target;
            Reset(now);
            return FillDecision.None;
        }

        if (input.Frozen)
        {
            frozenUntil = now + FrozenTailMs;
        }

        if (now < settleUntil)
        {
            State = FillState.Settling;
            return FillDecision.None;
        }

        if (!hasBaseline)
        {
            hasBaseline = true;
            latched = input.Count >= target;
            steadyCount = input.Count;
            candidateCount = input.Count;
            candidateSince = now;
            fullSince = -1;
            belowSince = -1;
            State = latched ? FillState.Latched : FillState.Armed;
            return FillDecision.None;
        }

        if (now < frozenUntil)
        {
            fullSince = -1;
            belowSince = -1;
            candidateCount = -1;
            if (input.Count >= target)
            {
                latched = true;
            }

            State = FillState.Frozen;
            return FillDecision.None;
        }

        TrackSteadyCount(input.Count, now);

        if (input.Count >= target)
        {
            belowSince = -1;
            if (latched)
            {
                State = FillState.Latched;
                return FillDecision.None;
            }

            if (fullSince < 0)
            {
                fullSince = now;
            }

            if ((now - fullSince) < StableMs)
            {
                State = FillState.Confirming;
                return FillDecision.None;
            }

            latched = true;
            fullSince = -1;
            State = FillState.Latched;
            return (steadyCount <= 1) ? FillDecision.SuppressedJoinedFull : FillDecision.Fire;
        }

        fullSince = -1;
        if (latched)
        {
            if (belowSince < 0)
            {
                belowSince = now;
            }

            if ((now - belowSince) >= RearmMs)
            {
                latched = false;
                belowSince = -1;
            }
        }

        State = latched ? FillState.Latched : FillState.Armed;
        return FillDecision.None;
    }

    /// <summary>Remember the last below-target count that held for <see cref="StableMs"/>, ignoring brief flickers.</summary>
    private void TrackSteadyCount(int count, long now)
    {
        if (count != candidateCount)
        {
            candidateCount = count;
            candidateSince = now;
            return;
        }

        if ((count < target) && ((now - candidateSince) >= StableMs))
        {
            steadyCount = count;
        }
    }

    private void Reset(long now)
    {
        hasBaseline = false;
        settleUntil = now + SettleMs;
        frozenUntil = long.MinValue;
        latched = false;
        fullSince = -1;
        belowSince = -1;
        candidateCount = -1;
        steadyCount = 0;
        State = FillState.Settling;
    }
}
