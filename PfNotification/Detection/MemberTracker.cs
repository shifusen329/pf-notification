using System.Collections.Generic;
using System.Linq;

namespace PfNotification.Detection;

/// <summary>Another party member (never the local player), keyed by content ID. The job is only used for messages.</summary>
public readonly record struct PartyMember(ulong ContentId, uint ClassJobId);

/// <summary>One observation of the other party members, taken on the framework thread.</summary>
/// <param name="NowMs">Monotonic milliseconds (<c>Environment.TickCount64</c>).</param>
/// <param name="Others">Party members other than the local player.</param>
/// <param name="LoggedIn">False on the title screen or while the local player isn't loaded.</param>
/// <param name="Frozen">In a duty or between areas: party data is in flux and must not produce events.</param>
public readonly record struct MemberInput(long NowMs, IReadOnlyList<PartyMember> Others, bool LoggedIn, bool Frozen);

/// <summary>Members confirmed to have joined or left since the last report.</summary>
public sealed record MemberChanges(IReadOnlyList<PartyMember> Joined, IReadOnlyList<PartyMember> Left);

/// <summary>
/// Reports members joining and leaving the party. Pure state machine with no game types, so it can be unit-tested.
/// Rules:
/// <list type="number">
/// <item>After login or load, wait <see cref="SettleMs"/> and take the party as it is as the baseline.</item>
/// <item>In a duty or zoning, plus <see cref="FrozenTailMs"/> after, follow the party silently: nothing is reported.</item>
/// <item>A join is reported once the member has been present for <see cref="JoinStableMs"/>; a leave once they've been
/// gone for <see cref="LeaveStableMs"/>, which rides out the cross-realm data briefly reading empty.</item>
/// <item>Going from solo to two or more members appearing together (within <see cref="JoinWaveMs"/>) means the player
/// joined a party: adopt it silently.</item>
/// <item>Two or more members vanishing at once, leaving the player solo, means they left or it disbanded: silent.</item>
/// <item>A member changing job is not a join or a leave.</item>
/// </list>
/// </summary>
public sealed class MemberTracker
{
    public const long JoinStableMs = FillDetector.StableMs;
    public const long LeaveStableMs = 5_000;
    public const long SettleMs = FillDetector.SettleMs;
    public const long FrozenTailMs = FillDetector.FrozenTailMs;
    public const long JoinWaveMs = 1_000;

    private readonly Dictionary<ulong, PartyMember> baseline = [];
    private readonly Dictionary<ulong, long> pendingJoin = [];
    private readonly Dictionary<ulong, long> pendingLeave = [];

    private bool loggedIn;
    private long settleUntil = long.MinValue;
    private long frozenUntil = long.MinValue;

    /// <summary>Other members currently in the confirmed party.</summary>
    public int Count => baseline.Count;

    /// <summary>Returns the confirmed changes, or null when there are none to report.</summary>
    public MemberChanges? Update(in MemberInput input)
    {
        var now = input.NowMs;

        if (!input.LoggedIn)
        {
            loggedIn = false;
            Clear();
            return null;
        }

        if (!loggedIn)
        {
            loggedIn = true;
            Clear();
            settleUntil = now + SettleMs;
        }

        if (input.Frozen)
        {
            frozenUntil = now + FrozenTailMs;
        }

        var current = new Dictionary<ulong, PartyMember>();
        foreach (var member in input.Others)
        {
            if (member.ContentId != 0)
            {
                current[member.ContentId] = member;
            }
        }

        if ((now < settleUntil) || (now < frozenUntil))
        {
            Adopt(current);
            return null;
        }

        // Existing members: keep their job current; a job change is not an event.
        foreach (var (id, member) in current)
        {
            if (baseline.ContainsKey(id))
            {
                baseline[id] = member;
                pendingLeave.Remove(id);
            }
            else
            {
                pendingJoin.TryAdd(id, now);
            }
        }

        foreach (var id in pendingJoin.Keys.Where(id => !current.ContainsKey(id)).ToList())
        {
            pendingJoin.Remove(id);
        }

        foreach (var id in baseline.Keys.Where(id => !current.ContainsKey(id)))
        {
            pendingLeave.TryAdd(id, now);
        }

        var joined = pendingJoin.Where(p => (now - p.Value) >= JoinStableMs).Select(p => current[p.Key]).ToList();
        var left = pendingLeave.Where(p => (now - p.Value) >= LeaveStableMs).Select(p => baseline[p.Key]).ToList();
        if ((joined.Count == 0) && (left.Count == 0))
        {
            return null;
        }

        // The player joined someone's party: from solo, several members appear together (within JoinWaveMs, as the list
        // can fill over a couple of ticks). Adopt it, including anyone still pending. pendingJoin still holds `joined`.
        if ((baseline.Count == 0) && (left.Count == 0))
        {
            var firstSeen = pendingJoin.Values.Min();
            if (pendingJoin.Values.Count(since => (since - firstSeen) <= JoinWaveMs) >= 2)
            {
                Adopt(current);
                return null;
            }
        }

        // The player left, or the party disbanded: everyone "vanishes" at once.
        if ((current.Count == 0) && (baseline.Count >= 2))
        {
            Adopt(current);
            return null;
        }

        foreach (var member in left)
        {
            baseline.Remove(member.ContentId);
            pendingLeave.Remove(member.ContentId);
        }

        foreach (var member in joined)
        {
            baseline[member.ContentId] = member;
            pendingJoin.Remove(member.ContentId);
        }

        return new MemberChanges(joined, left);
    }

    private void Adopt(Dictionary<ulong, PartyMember> current)
    {
        Clear();
        foreach (var (id, member) in current)
        {
            baseline[id] = member;
        }
    }

    private void Clear()
    {
        baseline.Clear();
        pendingJoin.Clear();
        pendingLeave.Clear();
    }
}
