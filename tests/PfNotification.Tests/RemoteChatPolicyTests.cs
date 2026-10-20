using PfNotification.Ntfy;
using PfNotification.RemoteChat;

namespace PfNotification.Tests;

public sealed class RemoteChatPolicyTests
{
    // Unix seconds when remote chat was enabled, and a monotonic clock for the ticks.
    private const long Start = 1_000_000;
    private const long T0 = 500_000;

    private static RemoteChatInput Input(long nowMs = T0, long nowUnix = Start + 10, bool loggedIn = true, bool zoning = false, int party = 4) =>
        new(nowMs, nowUnix, loggedIn, zoning, party);

    private static NtfyStreamMessage Message(string text, string id = "m1", long time = Start + 5, string ev = "message", string title = "") =>
        new(id, time, ev, "pf-alerts-chat", text, title);

    [Fact]
    public void AcceptedTextIsTrimmedAndPrefixed()
    {
        var policy = new RemoteChatPolicy(Start);

        var decision = policy.Evaluate(Message("  hello  "), Input());

        Assert.Equal(RemoteChatVerdict.Accept, decision.Verdict);
        Assert.Equal("hello", decision.Text);
        Assert.Equal("/p hello", decision.Line);
        Assert.Equal(string.Empty, decision.Reason);
    }

    [Fact]
    public void OtherEventsAreIgnored()
    {
        var policy = new RemoteChatPolicy(Start);

        Assert.Equal(RemoteChatVerdict.Ignore, policy.Evaluate(Message("x", ev: "keepalive"), Input()).Verdict);
        Assert.Equal(RemoteChatVerdict.Ignore, policy.Evaluate(Message("x", ev: "open"), Input()).Verdict);
    }

    [Fact]
    public void DuplicateIdIsIgnoredUntilItLeavesTheRecentWindow()
    {
        var policy = new RemoteChatPolicy(Start);
        var tick = T0;

        Assert.Equal(RemoteChatVerdict.Accept, policy.Evaluate(Message("a", id: "dup"), Input(tick)).Verdict);
        var again = policy.Evaluate(Message("a", id: "dup"), Input(tick += 4_000));
        Assert.Equal(RemoteChatVerdict.Ignore, again.Verdict);
        Assert.Equal("duplicate", again.Reason);

        // Spaced out so the flood window never trips; after RecentIds distinct ids the old one is forgotten.
        for (var i = 0; i < RemoteChatPolicy.RecentIds; i++)
        {
            Assert.Equal(RemoteChatVerdict.Accept, policy.Evaluate(Message("b", id: $"id{i}"), Input(tick += 4_000)).Verdict);
        }

        Assert.Equal(RemoteChatVerdict.Accept, policy.Evaluate(Message("a", id: "dup"), Input(tick + 4_000)).Verdict);
    }

    [Fact]
    public void MessagesFromBeforeEnablingAreRejected()
    {
        var policy = new RemoteChatPolicy(Start);

        var early = policy.Evaluate(Message("x", time: Start - RemoteChatPolicy.StartGraceSeconds - 1), Input());
        Assert.Equal(RemoteChatVerdict.Reject, early.Verdict);
        Assert.Equal("sent before remote chat was enabled", early.Reason);

        Assert.Equal(RemoteChatVerdict.Accept, policy.Evaluate(Message("x", id: "m2", time: Start - RemoteChatPolicy.StartGraceSeconds), Input()).Verdict);
    }

    [Theory]
    [InlineData(61, RemoteChatVerdict.Reject)]
    [InlineData(60, RemoteChatVerdict.Accept)]
    [InlineData(59, RemoteChatVerdict.Accept)]
    public void StaleMessagesAreRejected(long ageSeconds, RemoteChatVerdict expected)
    {
        var policy = new RemoteChatPolicy(Start);

        var decision = policy.Evaluate(Message("x", time: Start + 100), Input(nowUnix: Start + 100 + ageSeconds));

        Assert.Equal(expected, decision.Verdict);
        if (expected == RemoteChatVerdict.Reject)
        {
            Assert.Contains("too old", decision.Reason);
        }
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("   ", "empty")]
    [InlineData("a\nb", "control")]
    [InlineData("a\rb", "control")]
    [InlineData("a\tb", "control")]
    [InlineData("a\u0007b", "control")]
    [InlineData("/kick someone", "must not start with /")]
    [InlineData("  /p hi", "must not start with /")]
    public void ContentRulesReject(string text, string reasonFragment)
    {
        var policy = new RemoteChatPolicy(Start);

        var decision = policy.Evaluate(Message(text), Input());

        Assert.Equal(RemoteChatVerdict.Reject, decision.Verdict);
        Assert.Contains(reasonFragment, decision.Reason);
    }

    [Fact]
    public void TitleOnlyMessageIsRejectedWithAHint()
    {
        var policy = new RemoteChatPolicy(Start);

        var decision = policy.Evaluate(Message("", title: "hello"), Input());

        Assert.Equal(RemoteChatVerdict.Reject, decision.Verdict);
        Assert.Contains("not the title", decision.Reason);
    }

    [Theory]
    [InlineData("<3")]
    [InlineData("<flag> over here")]
    [InlineData("omw <se.1>")]
    public void PlaceholdersAndAngleBracketsAreAllowed(string text)
    {
        var policy = new RemoteChatPolicy(Start);

        var decision = policy.Evaluate(Message(text), Input());

        Assert.Equal(RemoteChatVerdict.Accept, decision.Verdict);
        Assert.Equal("/p " + text, decision.Line);
    }

    [Fact]
    public void LengthIsCountedInUtf8BytesIncludingThePrefix()
    {
        var policy = new RemoteChatPolicy(Start);
        var room = RemoteChatPolicy.MaxBytes - RemoteChatPolicy.Prefix.Length; // 497

        Assert.Equal(RemoteChatVerdict.Accept, policy.Evaluate(Message(new string('a', room), id: "a"), Input()).Verdict);

        var over = policy.Evaluate(Message(new string('a', room + 1), id: "b"), Input());
        Assert.Equal(RemoteChatVerdict.Reject, over.Verdict);
        Assert.Contains("too long", over.Reason);

        // 'é' is two bytes: 249 of them are 498 bytes, over with the prefix; 248 fit.
        Assert.Equal(RemoteChatVerdict.Reject, policy.Evaluate(Message(new string('é', 249), id: "c"), Input()).Verdict);
        Assert.Equal(RemoteChatVerdict.Accept, policy.Evaluate(Message(new string('é', 248), id: "d"), Input()).Verdict);
    }

    [Fact]
    public void ClientStateRules()
    {
        var policy = new RemoteChatPolicy(Start);

        var loggedOut = policy.Evaluate(Message("x", id: "a"), Input(loggedIn: false));
        Assert.Equal(RemoteChatVerdict.Reject, loggedOut.Verdict);
        Assert.Equal("not logged in", loggedOut.Reason);

        // Zoning defers even when the party reads empty: the count is unreliable mid-zone.
        Assert.Equal(RemoteChatVerdict.Defer, policy.Evaluate(Message("x", id: "b"), Input(zoning: true, party: 0)).Verdict);

        var solo = policy.Evaluate(Message("x", id: "c"), Input(party: 1));
        Assert.Equal(RemoteChatVerdict.Reject, solo.Verdict);
        Assert.Equal("not in a party", solo.Reason);
    }

    [Fact]
    public void MinimumIntervalDefersThenAccepts()
    {
        var policy = new RemoteChatPolicy(Start);
        Assert.Equal(RemoteChatVerdict.Accept, policy.Evaluate(Message("one", id: "a"), Input(T0)).Verdict);
        policy.MarkSent(T0);

        var second = Message("two", id: "b");
        Assert.Equal(RemoteChatVerdict.Defer, policy.Evaluate(second, Input(T0 + RemoteChatPolicy.MinIntervalMs - 1)).Verdict);
        Assert.Equal(RemoteChatVerdict.Accept, policy.Evaluate(second, Input(T0 + RemoteChatPolicy.MinIntervalMs)).Verdict);
    }

    [Fact]
    public void PerMinuteCapDefersUntilTheWindowSlides()
    {
        var policy = new RemoteChatPolicy(Start);
        var tick = T0;
        for (var i = 0; i < RemoteChatPolicy.MaxPerWindow; i++)
        {
            Assert.Equal(RemoteChatVerdict.Accept, policy.Evaluate(Message("x", id: $"m{i}"), Input(tick)).Verdict);
            policy.MarkSent(tick);
            tick += RemoteChatPolicy.MinIntervalMs;
        }

        var extra = Message("x", id: "extra");
        Assert.Equal(RemoteChatVerdict.Defer, policy.Evaluate(extra, Input(tick)).Verdict);
        Assert.Equal(RemoteChatVerdict.Accept, policy.Evaluate(extra, Input(T0 + RemoteChatPolicy.WindowMs)).Verdict);
    }

    [Fact]
    public void DeferredMessageBecomesStaleRatherThanWaitingForever()
    {
        var policy = new RemoteChatPolicy(Start);
        var message = Message("x", time: Start + 100);

        Assert.Equal(RemoteChatVerdict.Defer, policy.Evaluate(message, Input(nowUnix: Start + 110, zoning: true)).Verdict);

        var later = policy.Evaluate(message, Input(nowUnix: Start + 100 + RemoteChatPolicy.MaxAgeSeconds + 1, zoning: true));
        Assert.Equal(RemoteChatVerdict.Reject, later.Verdict);
        Assert.Contains("too old", later.Reason);
    }

    [Fact]
    public void FloodIsIgnoredAndWarnedOncePerWindow()
    {
        var warnings = new List<string>();
        var policy = new RemoteChatPolicy(Start, warnings.Add);
        for (var i = 0; i < RemoteChatPolicy.MaxInboundPerWindow; i++)
        {
            Assert.Equal(RemoteChatVerdict.Accept, policy.Evaluate(Message("x", id: $"m{i}"), Input()).Verdict);
        }

        var flood = policy.Evaluate(Message("x", id: "f1"), Input());
        Assert.Equal(RemoteChatVerdict.Ignore, flood.Verdict);
        Assert.Equal("flood", flood.Reason);
        Assert.Equal(RemoteChatVerdict.Ignore, policy.Evaluate(Message("x", id: "f2"), Input()).Verdict);
        Assert.Single(warnings);

        // Once the minute has passed, messages flow again.
        Assert.Equal(RemoteChatVerdict.Accept, policy.Evaluate(Message("x", id: "f3"), Input(T0 + RemoteChatPolicy.WindowMs)).Verdict);
    }

    [Fact]
    public void RejectionsDoNotUseTheRateBudget()
    {
        var policy = new RemoteChatPolicy(Start);
        Assert.Equal(RemoteChatVerdict.Accept, policy.Evaluate(Message("x", id: "a"), Input(T0)).Verdict);
        policy.MarkSent(T0);

        var later = T0 + RemoteChatPolicy.MinIntervalMs;
        Assert.Equal(RemoteChatVerdict.Reject, policy.Evaluate(Message("/cmd", id: "b"), Input(later)).Verdict);
        Assert.Equal(RemoteChatVerdict.Accept, policy.Evaluate(Message("fine", id: "c"), Input(later)).Verdict);
    }
}
