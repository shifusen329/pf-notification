using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using PfNotification.Ntfy;

namespace PfNotification.RemoteChat;

/// <summary>Live client state at the moment a phone message is considered. <see cref="NowMs"/> is monotonic.</summary>
/// <param name="NowUnix">Local wall clock, unix seconds, compared with the message's server timestamp.</param>
/// <param name="Zoning">Between areas: the party count is unreliable, so wait rather than refuse.</param>
public readonly record struct RemoteChatInput(long NowMs, long NowUnix, bool LoggedIn, bool Zoning, int PartyCount);

public enum RemoteChatVerdict
{
    /// <summary>Send <see cref="RemoteChatDecision.Line"/> now.</summary>
    Accept,

    /// <summary>Drop it and tell the phone why.</summary>
    Reject,

    /// <summary>Keep it at the head of the queue and ask again next tick.</summary>
    Defer,

    /// <summary>Drop it silently (not a message, a duplicate, or a flood).</summary>
    Ignore,
}

/// <param name="Text">The trimmed message text, for feedback.</param>
/// <param name="Line">The exact chat line to send; empty unless accepted.</param>
/// <param name="Reason">Why, in words the phone user understands; empty when accepted.</param>
public sealed record RemoteChatDecision(RemoteChatVerdict Verdict, string Text, string Line, string Reason);

/// <summary>
/// Every guard between a message published to the chat topic and the party chat line the client sends. Pure, so it
/// can be unit-tested. Rules, in order:
/// <list type="number">
/// <item>Only <c>message</c> events; a duplicate id (catch-up overlap) or more than <see cref="MaxInboundPerWindow"/>
/// messages a minute are ignored.</item>
/// <item>Messages published before remote chat was enabled, or more than <see cref="MaxAgeSeconds"/> ago, are rejected:
/// nothing typed while the game was closed is ever spoken.</item>
/// <item>Text must be non-empty, one line, not a command (no leading '/'), and fit in <see cref="MaxBytes"/> UTF-8
/// bytes with the <see cref="Prefix"/>. Placeholders such as &lt;flag&gt; are allowed: they're ordinary chat.</item>
/// <item>Logged in and in a party; while zoning, wait instead.</item>
/// <item>At most one send per <see cref="MinIntervalMs"/> and <see cref="MaxPerWindow"/> per minute; beyond that,
/// wait. A deferred message that waits past <see cref="MaxAgeSeconds"/> becomes a stale rejection.</item>
/// </list>
/// </summary>
public sealed class RemoteChatPolicy
{
    public const string Prefix = "/p ";
    public const int MaxBytes = 500;
    public const long MaxAgeSeconds = 60;
    public const long StartGraceSeconds = 5;
    public const long MinIntervalMs = 2_000;
    public const int MaxPerWindow = 10;
    public const long WindowMs = 60_000;
    public const int MaxInboundPerWindow = 20;
    public const int RecentIds = 32;

    private readonly long startedAtUnix;
    private readonly Action<string> warn;
    private readonly Queue<string> recentIds = new();
    private readonly HashSet<string> recentIdSet = new(StringComparer.Ordinal);
    private readonly Queue<long> inboundAt = new();
    private readonly Queue<long> sentAt = new();

    private NtfyStreamMessage? deferred;
    private long? lastSentMs;
    private bool floodWarned;

    /// <param name="startedAtUnix">When remote chat was enabled; earlier messages are rejected.</param>
    /// <param name="warn">Receives the one warning per flood window.</param>
    public RemoteChatPolicy(long startedAtUnix, Action<string>? warn = null)
    {
        this.startedAtUnix = startedAtUnix;
        this.warn = warn ?? (_ => { });
    }

    public RemoteChatDecision Evaluate(NtfyStreamMessage message, in RemoteChatInput input)
    {
        var text = message.Message.Trim();
        if (message.Event != NtfyStreamMessage.MessageEvent)
        {
            return Settle(RemoteChatVerdict.Ignore, text, "not a message");
        }

        // Admission runs once per message; a deferred message is the same instance asked about again.
        if (!ReferenceEquals(message, deferred) && (Admit(message, input.NowMs) is { } ignored))
        {
            return Settle(RemoteChatVerdict.Ignore, text, ignored);
        }

        if (message.Time < (startedAtUnix - StartGraceSeconds))
        {
            return Settle(RemoteChatVerdict.Reject, text, "sent before remote chat was enabled");
        }

        var age = input.NowUnix - message.Time;
        if (age > MaxAgeSeconds)
        {
            return Settle(RemoteChatVerdict.Reject, text, $"too old ({age} s)");
        }

        if (ValidateText(message.Message, out text) is { } problem)
        {
            return Settle(RemoteChatVerdict.Reject, text, problem);
        }

        if (!input.LoggedIn)
        {
            return Settle(RemoteChatVerdict.Reject, text, "not logged in");
        }

        if (input.Zoning)
        {
            return Defer(message, text, "zoning");
        }

        if (input.PartyCount < 2)
        {
            return Settle(RemoteChatVerdict.Reject, text, "not in a party");
        }

        if (IsRateLimited(input.NowMs))
        {
            return Defer(message, text, "rate limited");
        }

        deferred = null;
        return new RemoteChatDecision(RemoteChatVerdict.Accept, text, Prefix + text, string.Empty);
    }

    /// <summary>Call after the line actually went out, so refused sends don't use up the budget.</summary>
    public void MarkSent(long nowMs)
    {
        sentAt.Enqueue(nowMs);
        lastSentMs = nowMs;
    }

    /// <summary>Content rules only. Returns the problem, or null when <paramref name="trimmed"/> can be sent.</summary>
    public static string? ValidateText(string raw, out string trimmed)
    {
        trimmed = raw.Trim();
        if (trimmed.Length == 0)
        {
            return "empty message (put the text in the message, not the title)";
        }

        if (trimmed.Any(char.IsControl))
        {
            return "contains line breaks or control characters";
        }

        if (trimmed[0] == '/')
        {
            return "must not start with / (party chat only)";
        }

        var bytes = Encoding.UTF8.GetByteCount(Prefix) + Encoding.UTF8.GetByteCount(trimmed);
        if (bytes > MaxBytes)
        {
            return $"too long ({bytes}/{MaxBytes} bytes)";
        }

        return null;
    }

    private RemoteChatDecision Settle(RemoteChatVerdict verdict, string text, string reason)
    {
        deferred = null;
        return new RemoteChatDecision(verdict, text, string.Empty, reason);
    }

    private RemoteChatDecision Defer(NtfyStreamMessage message, string text, string reason)
    {
        deferred = message;
        return new RemoteChatDecision(RemoteChatVerdict.Defer, text, string.Empty, reason);
    }

    /// <summary>Dedupe and flood control. Returns why the message is ignored, or null to go on.</summary>
    private string? Admit(NtfyStreamMessage message, long nowMs)
    {
        if (message.Id.Length > 0)
        {
            if (!recentIdSet.Add(message.Id))
            {
                return "duplicate";
            }

            recentIds.Enqueue(message.Id);
            if (recentIds.Count > RecentIds)
            {
                recentIdSet.Remove(recentIds.Dequeue());
            }
        }

        Prune(inboundAt, nowMs);
        inboundAt.Enqueue(nowMs);
        if (inboundAt.Count > MaxInboundPerWindow)
        {
            if (!floodWarned)
            {
                floodWarned = true;
                warn($"more than {MaxInboundPerWindow} messages in a minute; ignoring the rest");
            }

            return "flood";
        }

        floodWarned = false;
        return null;
    }

    private bool IsRateLimited(long nowMs)
    {
        Prune(sentAt, nowMs);
        if (sentAt.Count >= MaxPerWindow)
        {
            return true;
        }

        return (lastSentMs is { } last) && ((nowMs - last) < MinIntervalMs);
    }

    private static void Prune(Queue<long> times, long nowMs)
    {
        while ((times.Count > 0) && ((nowMs - times.Peek()) >= WindowMs))
        {
            times.Dequeue();
        }
    }
}
