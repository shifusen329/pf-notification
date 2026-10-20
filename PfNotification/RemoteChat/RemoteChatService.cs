using System;
using Dalamud.Plugin.Services;
using PfNotification.Ntfy;

namespace PfNotification.RemoteChat;

/// <summary>The most recent phone message and what became of it, for the window.</summary>
public sealed record RemoteChatReport(DateTime At, RemoteChatVerdict Verdict, string Text, string Reason);

/// <summary>
/// Runs remote party chat: keeps an <see cref="NtfySubscriber"/> on the chat topic while the feature is on and its
/// settings are valid, and on every framework tick drains what arrived through <see cref="RemoteChatPolicy"/> and
/// <see cref="ChatSender"/>. Reports back to the phone through the alerts topic and to the player in chat.
/// Everything here runs on the framework thread except the subscriber's own loop.
/// </summary>
public sealed class RemoteChatService : IDisposable
{
    /// <summary>Settings edits are applied once they've stopped changing for this long (the window saves per keystroke).</summary>
    public const long SettleMs = 1_500;

    public const int MaxEvaluationsPerTick = 8;

    private const int EchoMaxChars = 120;
    private const string LogPrefix = "[chat]";

    private static readonly string[] SentTags = ["speech_balloon"];
    private static readonly string[] RejectedTags = ["no_entry"];

    private readonly Configuration config;
    private readonly string userAgent;
    private readonly IPluginLog log;
    private readonly IChatGui chat;
    private readonly Action<NtfyMessage, SendKind> publish;

    private NtfySubscriber? subscriber;
    private RemoteChatPolicy? policy;
    private NtfySettings? active;
    private NtfySettings? pending;
    private bool hasPending;
    private long pendingSince;
    private SubscriberState announcedState;
    private bool connectedAnnounced;
    private volatile RemoteChatReport? lastReport;
    private bool disposed;

    /// <param name="publish">The plugin's send path for feedback to the phone (the alerts topic).</param>
    public RemoteChatService(Configuration config, string userAgent, IPluginLog log, IChatGui chat, Action<NtfyMessage, SendKind> publish)
    {
        this.config = config;
        this.userAgent = userAgent;
        this.log = log;
        this.chat = chat;
        this.publish = publish;
    }

    /// <summary>Null while the feature is off or its settings are invalid.</summary>
    public SubscriberStatus? Status => subscriber?.Status;

    public string? ActiveTopic => active?.Topic;

    public RemoteChatReport? LastReport => lastReport;

    /// <summary>Framework thread only.</summary>
    public void Tick(long nowMs, bool loggedIn, bool zoning, int partyCount)
    {
        if (disposed)
        {
            return;
        }

        Reconcile(nowMs);
        if ((subscriber is null) || (policy is null))
        {
            return;
        }

        AnnounceStateChanges();
        Drain(nowMs, loggedIn, zoning, partyCount);
    }

    public void Dispose()
    {
        disposed = true;
        subscriber?.Stop();
        subscriber = null;
        policy = null;
    }

    public static string Describe(SubscriberStatus? status) => status is null
        ? "off"
        : status.State switch
        {
            SubscriberState.Stopped => "starting",
            SubscriberState.Connecting => "connecting",
            SubscriberState.Connected => $"connected since {status.Since:HH:mm:ss}",
            SubscriberState.Reconnecting => $"reconnecting (attempt {status.Attempt}): {status.Detail}",
            SubscriberState.AuthRejected => "token rejected",
            SubscriberState.Forbidden => "token has no read access to the chat topic",
            _ => $"stopped: {status.Detail}",
        };

    /// <summary>Starts, restarts or stops the subscriber to match the settings, once they've settled.</summary>
    private void Reconcile(long nowMs)
    {
        var desired = (config.RemoteChatEnabled && (config.GetRemoteChatProblem() is null)) ? config.ToRemoteChatSettings() : null;
        if (desired == active)
        {
            hasPending = false;
            return;
        }

        if (!hasPending || (desired != pending))
        {
            hasPending = true;
            pending = desired;
            pendingSince = nowMs;
            return;
        }

        if ((nowMs - pendingSince) < SettleMs)
        {
            return;
        }

        hasPending = false;
        Apply(desired);
    }

    private void Apply(NtfySettings? desired)
    {
        subscriber?.Stop();
        subscriber = null;
        policy = null;
        active = desired;
        announcedState = SubscriberState.Stopped;
        connectedAnnounced = false;

        if (desired is null)
        {
            log.Information("{Prefix} remote chat off", LogPrefix);
            return;
        }

        policy = new RemoteChatPolicy(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), reason => log.Warning("{Prefix} {Reason}", LogPrefix, reason));
        subscriber = new NtfySubscriber(desired, userAgent, line => log.Information("{Prefix} {Line}", LogPrefix, line));
        subscriber.Start();
        log.Information("{Prefix} subscribing to {Topic}", LogPrefix, desired.Topic);
    }

    private void AnnounceStateChanges()
    {
        var status = subscriber!.Status;
        if (status.State == announcedState)
        {
            return;
        }

        announcedState = status.State;
        switch (status.State)
        {
            case SubscriberState.Connected:
                log.Information("{Prefix} connected to {Topic}", LogPrefix, active!.Topic);
                if (!connectedAnnounced)
                {
                    connectedAnnounced = true;
                    if (config.ChatConfirmation)
                    {
                        chat.Print($"Remote chat connected: messages published to {active.Topic} are posted to party chat.", Plugin.ChatTag);
                    }
                }

                break;
            case SubscriberState.Reconnecting:
                log.Warning("{Prefix} {Detail}", LogPrefix, status.Detail);
                break;
            case SubscriberState.AuthRejected:
            case SubscriberState.Forbidden:
            case SubscriberState.Failed:
                log.Warning("{Prefix} stopped: {Detail}", LogPrefix, status.Detail);
                chat.PrintError($"Remote chat stopped: {status.Detail}. Fix the settings in /pfnotify to retry.", Plugin.ChatTag);
                break;
        }
    }

    private void Drain(long nowMs, bool loggedIn, bool zoning, int partyCount)
    {
        var input = new RemoteChatInput(nowMs, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), loggedIn, zoning, partyCount);
        for (var i = 0; (i < MaxEvaluationsPerTick) && subscriber!.TryPeek(out var message); i++)
        {
            var decision = policy!.Evaluate(message, input);
            if (decision.Verdict == RemoteChatVerdict.Defer)
            {
                return;
            }

            subscriber.TryDequeue(out _);
            switch (decision.Verdict)
            {
                case RemoteChatVerdict.Ignore:
                    log.Debug("{Prefix} ignored message {Id}: {Reason}", LogPrefix, message.Id, decision.Reason);
                    break;
                case RemoteChatVerdict.Reject:
                    Report(RemoteChatVerdict.Reject, decision.Text, decision.Reason);
                    break;
                case RemoteChatVerdict.Accept:
                    var outcome = ChatSender.SendLine(decision.Line);
                    if (outcome == ChatSendOutcome.Sent)
                    {
                        policy.MarkSent(nowMs);
                        Report(RemoteChatVerdict.Accept, decision.Text, string.Empty);
                        return; // one line per tick
                    }

                    Report(RemoteChatVerdict.Reject, decision.Text, Describe(outcome));
                    break;
            }
        }
    }

    private void Report(RemoteChatVerdict verdict, string text, string reason)
    {
        lastReport = new RemoteChatReport(DateTime.Now, verdict, text, reason);
        var shown = (text.Length > EchoMaxChars) ? (text[..EchoMaxChars] + "…") : text;
        if (verdict == RemoteChatVerdict.Accept)
        {
            log.Information("{Prefix} posted to party: {Text}", LogPrefix, shown);
            if (config.ChatConfirmation)
            {
                chat.Print("Posted to party from your phone.", Plugin.ChatTag);
            }

            publish(new NtfyMessage("Sent to party", shown, 2, SentTags), SendKind.ChatFeedback);
            return;
        }

        log.Warning("{Prefix} rejected ({Reason}): {Text}", LogPrefix, reason, shown);
        chat.PrintError($"Remote chat rejected ({reason}): {shown}", Plugin.ChatTag);
        publish(new NtfyMessage("Not sent to party", $"{reason}: {shown}", 3, RejectedTags), SendKind.ChatFeedback);
    }

    private static string Describe(ChatSendOutcome outcome) => outcome switch
    {
        ChatSendOutcome.Sanitised => "rejected by the game's text filter",
        ChatSendOutcome.TooLong => "too long",
        ChatSendOutcome.Empty => "empty message",
        _ => "client not ready",
    };
}
