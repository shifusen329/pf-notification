using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using PfNotification.Detection;
using PfNotification.Diagnostics;
using PfNotification.Ntfy;
using PfNotification.Party;
using PfNotification.RemoteChat;
using PfNotification.Windows;

namespace PfNotification;

/// <summary>Live state for the window: published by the framework thread, read by Draw.</summary>
public sealed record PluginStatus(PartySnapshot Party, FillState State, int Target);

public enum SendKind
{
    Test,
    Full,
    Change,
    DutyPop,
    ChatFeedback,
}

/// <summary>Outcome of the most recent send.</summary>
public sealed record SendReport(DateTime At, NtfyResult Result, SendKind Kind);

/// <summary>
/// Watches the party on the framework thread and sends ntfy alerts when it fills, members come and go, or a duty pops.
/// Passive by default (see docs/dalamud-api.md): it reads client state and prints locally, and never sends anything to
/// the game servers. The one exception is remote party chat, which is off unless the player turns it on; see
/// <see cref="RemoteChatService"/>.
/// </summary>
public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IPartyList PartyList { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static INotificationManager NotificationManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    internal const string ChatTag = "PF Notification";

    private const string CommandName = "/pfnotify";
    private const long PollIntervalMs = 250;
    private const long AutoSendCooldownMs = 60_000;
    private const long DutyPopDebounceMs = 10_000;

    // A join that completes the party is covered by the fill alert, which fires within this window of it.
    private const long FillOverlapMs = 5_000;

    private static readonly string[] AlertTags = ["busts_in_silhouette"];
    private static readonly string[] JoinTags = ["heavy_plus_sign"];
    private static readonly string[] LeaveTags = ["heavy_minus_sign"];
    private static readonly string[] DutyTags = ["bell"];

    // In a duty or zoning, party data is in flux (Duty Finder parties, cross-realm to party-list switching).
    private static readonly ConditionFlag[] FrozenFlags =
    [
        ConditionFlag.BoundByDuty,
        ConditionFlag.BoundByDuty56,
        ConditionFlag.BoundByDuty95,
        ConditionFlag.BetweenAreas,
        ConditionFlag.BetweenAreas51,
    ];

    // Party chat works inside duties, so remote chat only waits while actually zoning.
    private static readonly ConditionFlag[] ZoningFlags =
    [
        ConditionFlag.BetweenAreas,
        ConditionFlag.BetweenAreas51,
    ];

    private static readonly string Usage =
        $"Usage: {CommandName} [test | on | off | joins on|off | duty on|off | chat on|off | status | target <{Configuration.MinTargetSize}-{Configuration.MaxTargetSize}>]";

    public readonly WindowSystem WindowSystem = new("PfNotification");

    private readonly FillDetector detector = new();
    private readonly MemberTracker memberTracker = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly NtfyClient ntfy;
    private readonly DiagnosticLogger diagnostics;
    private readonly RemoteChatService remoteChat;

    private long nextPollAt;
    private long lastAutoSendAt = -AutoSendCooldownMs;
    private long lastFillAt = -FillOverlapMs;
    private long lastDutyPopAt = -DutyPopDebounceMs;
    private PartySnapshot lastSnapshot;
    private volatile bool disposed;
    private volatile bool testRunning;
    private volatile PluginStatus? status;
    private volatile SendReport? lastSend;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        var userAgent = $"PfNotification/{PluginInterface.Manifest.AssemblyVersion}";
        ntfy = new NtfyClient(userAgent);
        diagnostics = new DiagnosticLogger(ChatGui, Condition, Log);
        diagnostics.SetEnabled(Configuration.DiagnosticLogging);
        remoteChat = new RemoteChatService(Configuration, userAgent, Log, ChatGui, StartSend);

        MainWindow = new MainWindow(this);
        WindowSystem.AddWindow(MainWindow);

        if (!CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = $"Open PF Notification. \"{CommandName} test\" sends a test alert, \"on\"/\"off\" toggles alerts, " +
                          $"\"joins on|off\" and \"duty on|off\" toggle join/leave and duty-pop alerts, \"chat on|off\" toggles " +
                          $"remote party chat, \"status\" shows the party state, \"target 4\" sets the full size.",
        }))
        {
            Log.Warning("Could not register {Command}: another plugin already owns it", CommandName);
        }

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleMainUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;

        ClientState.CfPop += OnCfPop;
        Framework.Update += OnFrameworkUpdate;
    }

    public Configuration Configuration { get; init; }

    public PluginStatus? Status => status;

    public SendReport? LastSend => lastSend;

    public bool IsTestRunning => testRunning;

    public SubscriberStatus? RemoteChatStatus => remoteChat.Status;

    public string? RemoteChatTopic => remoteChat.ActiveTopic;

    public RemoteChatReport? LastRemoteChat => remoteChat.LastReport;

    private MainWindow MainWindow { get; init; }

    public void Dispose()
    {
        disposed = true;
        Framework.Update -= OnFrameworkUpdate;
        ClientState.CfPop -= OnCfPop;
        lifetime.Cancel();

        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleMainUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;

        WindowSystem.RemoveAllWindows();
        MainWindow.Dispose();

        CommandManager.RemoveHandler(CommandName);

        remoteChat.Dispose();
        diagnostics.Dispose();
        ntfy.Dispose();
        lifetime.Dispose();
    }

    public void ToggleMainUi() => MainWindow.Toggle();

    /// <summary>Send a test notification with the current settings. Safe to call from any thread.</summary>
    public void SendTest()
    {
        if (testRunning)
        {
            return;
        }

        testRunning = true;
        StartSend(
            new NtfyMessage(
                "PF Notification test",
                $"Test from {CommandName}. Party alerts will arrive like this.",
                Configuration.Priority,
                AlertTags),
            SendKind.Test);
    }

    public void SetDiagnostics(bool enabled)
    {
        Configuration.DiagnosticLogging = enabled;
        Configuration.Save();
        _ = Framework.RunOnFrameworkThread(() =>
        {
            if (!disposed)
            {
                diagnostics.SetEnabled(enabled);
            }
        });
    }

    public static string DescribeState(FillState state) => state switch
    {
        FillState.Settling => "settling (waiting for party data)",
        FillState.Frozen => "paused (in a duty or zoning)",
        FillState.Armed => "armed: alerts when the party fills",
        FillState.Confirming => "party full, confirming…",
        FillState.Latched => "alerted (re-arms when a slot opens)",
        _ => state.ToString(),
    };

    public static string DescribeSource(PartySnapshot party) => party.Source switch
    {
        PartySource.PartyList => "party list",
        PartySource.CrossRealm => party.SelfAdded ? "cross-world, you added" : "cross-world",
        _ => "solo",
    };

    public static string Describe(SendKind kind) => kind switch
    {
        SendKind.Test => "Test notification",
        SendKind.Full => "Party-full alert",
        SendKind.DutyPop => "Duty-ready alert",
        SendKind.ChatFeedback => "Remote chat confirmation",
        _ => "Join/leave alert",
    };

    private void OnFrameworkUpdate(IFramework framework)
    {
        var now = Environment.TickCount64;
        if (now < nextPollAt)
        {
            return;
        }

        nextPollAt = now + PollIntervalMs;

        var loggedIn = ClientState.IsLoggedIn && PlayerState.IsLoaded;
        var snapshot = loggedIn ? PartyReader.Read(PartyList, PlayerState.ContentId) : default;
        var frozen = Condition.Any(FrozenFlags);
        var target = Configuration.GetTargetSize();
        var decision = detector.Update(new FillInput(now, snapshot.Count, target, loggedIn, frozen));
        var others = loggedIn ? PartyReader.ReadOthers(PartyList, PlayerState.ContentId) : [];
        var changes = memberTracker.Update(new MemberInput(now, others, loggedIn, frozen));

        if (snapshot != lastSnapshot)
        {
            diagnostics.OnPartyChanged(lastSnapshot, snapshot, frozen);
            lastSnapshot = snapshot;
        }

        status = new PluginStatus(snapshot, detector.State, target);

        switch (decision)
        {
            case FillDecision.Fire:
                lastFillAt = now;
                OnPartyFilled(now, snapshot, target);
                break;
            case FillDecision.SuppressedJoinedFull:
                Log.Information("Joined a full party ({Count}/{Target}); no alert", snapshot.Count, target);
                break;
        }

        if (changes is not null)
        {
            OnMembersChanged(now, changes, snapshot, target);
        }

        remoteChat.Tick(now, loggedIn, Condition.Any(ZoningFlags), snapshot.Count);
    }

    /// <summary>Dalamud raises this from a background task, so only copy the name here and decide on the framework thread.</summary>
    private void OnCfPop(ContentFinderCondition duty)
    {
        string name;
        try
        {
            name = AlertText.DutyName(duty.Name.ExtractText());
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not read the duty name; treating the pop as a roulette");
            name = AlertText.RouletteName;
        }

        _ = Framework.RunOnFrameworkThread(() => HandleDutyPop(name));
    }

    private void HandleDutyPop(string name)
    {
        if (disposed)
        {
            return;
        }

        var now = Environment.TickCount64;
        if ((now - lastDutyPopAt) < DutyPopDebounceMs)
        {
            Log.Debug("Duty pop ({Name}) within {Seconds} s of the last; skipped", name, DutyPopDebounceMs / 1000);
            return;
        }

        lastDutyPopAt = now;
        if (!Configuration.Enabled || !Configuration.DutyPopAlerts)
        {
            Log.Information("Duty ready ({Name}); duty-pop alerts are off", name);
            return;
        }

        Log.Information("Duty ready ({Name}); sending alert", name);
        StartSend(new NtfyMessage(AlertText.DutyTitle, AlertText.DutyBody(name), Configuration.DutyPopPriority, DutyTags), SendKind.DutyPop);
    }

    private void OnMembersChanged(long now, MemberChanges changes, PartySnapshot snapshot, int target)
    {
        var classJobs = DataManager.GetExcelSheet<ClassJob>();
        var joined = changes.Joined.Select(m => PartyReader.JobAbbreviation(classJobs, m.ClassJobId)).ToList();
        var left = changes.Left.Select(m => PartyReader.JobAbbreviation(classJobs, m.ClassJobId)).ToList();
        var title = AlertText.ChangeTitle(joined, left);
        Log.Information("{Change} ({Count}/{Target})", title, snapshot.Count, target);

        if (!Configuration.Enabled || !Configuration.JoinLeaveAlerts)
        {
            return;
        }

        // The member who completed the party is announced by the fill alert, which fires around the same tick.
        var completesParty = (changes.Left.Count == 0) && (snapshot.Count >= target);
        if (completesParty && ((detector.State == FillState.Confirming) || ((now - lastFillAt) < FillOverlapMs)))
        {
            return;
        }

        var roles = PartyReader.ReadRoles(PartyList, classJobs, PlayerState.ContentId, PlayerState.ClassJob.RowId);
        StartSend(
            new NtfyMessage(
                title,
                AlertText.Summary(snapshot.Count, target, roles),
                Configuration.JoinLeavePriority,
                (changes.Joined.Count > 0) ? JoinTags : LeaveTags),
            SendKind.Change);
    }

    private void OnPartyFilled(long now, PartySnapshot snapshot, int target)
    {
        if (!Configuration.Enabled)
        {
            Log.Information("Party full ({Count}/{Target}); alerts are off", snapshot.Count, target);
            return;
        }

        if ((now - lastAutoSendAt) < AutoSendCooldownMs)
        {
            Log.Warning("Party full again within {Seconds} s of the last alert; skipped", AutoSendCooldownMs / 1000);
            return;
        }

        lastAutoSendAt = now;
        var roles = PartyReader.ReadRoles(PartyList, DataManager.GetExcelSheet<ClassJob>(), PlayerState.ContentId, PlayerState.ClassJob.RowId);
        Log.Information("Party full ({Count}/{Target}: {Roles}); sending alert", snapshot.Count, target, roles.ToString());
        StartSend(new NtfyMessage(AlertText.FullTitle, AlertText.Summary(snapshot.Count, target, roles), Configuration.Priority, AlertTags), SendKind.Full);
    }

    /// <summary>Publish off the framework thread, then report back on it. Settings are captured now.</summary>
    private void StartSend(NtfyMessage message, SendKind kind)
    {
        var settings = Configuration.ToNtfySettings();
        var ct = lifetime.Token;
        _ = Task.Run(async () =>
        {
            NtfyResult result;
            try
            {
                result = await ntfy.PublishAsync(settings, message, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                result = new NtfyResult(NtfyOutcome.NetworkError, null, 0, ex.Message);
            }
            finally
            {
                if (kind == SendKind.Test)
                {
                    testRunning = false;
                }
            }

            if (disposed || (result.Outcome == NtfyOutcome.Cancelled))
            {
                return;
            }

            lastSend = new SendReport(DateTime.Now, result, kind);
            if (result.Ok)
            {
                Log.Information("{Kind} sent (attempt {Attempts})", Describe(kind), result.Attempts);
            }
            else
            {
                Log.Warning("{Kind} failed: {Outcome} after {Attempts} attempt(s): {Detail}", Describe(kind), result.Outcome, result.Attempts, result.Detail);
            }

            _ = Framework.RunOnFrameworkThread(() => ReportResult(result, kind));
        });
    }

    private void ReportResult(NtfyResult result, SendKind kind)
    {
        // Join/leave alerts and chat confirmations can come in bursts; only interrupt the player when one fails.
        if (disposed || (result.Ok && (kind is SendKind.Change or SendKind.ChatFeedback)))
        {
            return;
        }

        var what = Describe(kind);
        var text = result.Ok ? $"{what} sent to your phone." : $"{what} failed: {result.Detail}";
        NotificationManager.AddNotification(new Notification
        {
            Title = ChatTag,
            Content = text,
            Type = result.Ok ? NotificationType.Success : NotificationType.Error,
        });

        if (!result.Ok)
        {
            ChatGui.PrintError(text, ChatTag);
        }
        else if (Configuration.ChatConfirmation)
        {
            ChatGui.Print(text, ChatTag);
        }
    }

    // Everything printed here goes to the player's own chat log only; nothing is sent to the server.
    private void OnCommand(string command, string args)
    {
        var parts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var sub = (parts.Length == 0) ? string.Empty : parts[0].ToLowerInvariant();
        var arg = (parts.Length == 2) ? parts[1].ToLowerInvariant() : string.Empty;
        switch (sub)
        {
            case "":
                ToggleMainUi();
                return;
            case "test":
                if (Configuration.GetConfigProblem() is { } problem)
                {
                    ChatGui.PrintError($"Not configured: {problem}. Open {CommandName} to set it up.", ChatTag);
                    return;
                }

                ChatGui.Print("Sending a test notification…", ChatTag);
                SendTest();
                return;
            case "on":
            case "off":
                Configuration.Enabled = sub == "on";
                Configuration.Save();
                ChatGui.Print($"Alerts {sub}.", ChatTag);
                return;
            case "joins":
                if (arg is "on" or "off")
                {
                    Configuration.JoinLeaveAlerts = arg == "on";
                    Configuration.Save();
                    ChatGui.Print($"Join/leave alerts {arg}.", ChatTag);
                }
                else
                {
                    ChatGui.PrintError(Usage, ChatTag);
                }

                return;
            case "duty":
                if (arg is "on" or "off")
                {
                    Configuration.DutyPopAlerts = arg == "on";
                    Configuration.Save();
                    ChatGui.Print($"Duty-pop alerts {arg}.", ChatTag);
                }
                else
                {
                    ChatGui.PrintError(Usage, ChatTag);
                }

                return;
            case "chat":
                SetRemoteChat(arg);
                return;
            case "status":
                ChatGui.Print(DescribeStatus(), ChatTag);
                return;
            case "target":
                if (int.TryParse(arg, out var size) && (size >= Configuration.MinTargetSize) && (size <= Configuration.MaxTargetSize))
                {
                    Configuration.TargetSize = size;
                    Configuration.Save();
                    ChatGui.Print($"Alerts when the party reaches {size}.", ChatTag);
                }
                else
                {
                    ChatGui.PrintError(Usage, ChatTag);
                }

                return;
            default:
                ChatGui.PrintError(Usage, ChatTag);
                return;
        }
    }

    private void SetRemoteChat(string arg)
    {
        switch (arg)
        {
            case "on":
                if (Configuration.GetRemoteChatProblem() is { } problem)
                {
                    ChatGui.PrintError($"Remote chat can't start: {problem}. Open {CommandName} to fix it.", ChatTag);
                    return;
                }

                Configuration.RemoteChatEnabled = true;
                Configuration.Save();
                ChatGui.Print($"Remote chat on: messages published to {Configuration.GetRemoteChatTopic()} will be posted to party chat as /p.", ChatTag);
                return;
            case "off":
                Configuration.RemoteChatEnabled = false;
                Configuration.Save();
                ChatGui.Print("Remote chat off.", ChatTag);
                return;
            default:
                ChatGui.PrintError(Usage, ChatTag);
                return;
        }
    }

    private string DescribeStatus()
    {
        var alerts = Configuration.Enabled ? (Configuration.JoinLeaveAlerts ? "on (with joins/leaves)" : "on (party full only)") : "off";
        var duty = Configuration.DutyPopAlerts ? "on" : "off";
        var chat = Configuration.RemoteChatEnabled ? RemoteChatService.Describe(remoteChat.Status) : "off";
        var problem = Configuration.GetConfigProblem();
        var setup = (problem is null) ? string.Empty : $" Not configured: {problem}.";
        if (status is not { } s)
        {
            return $"Alerts {alerts}; duty pops {duty}; remote chat {chat}; waiting for the game.{setup}";
        }

        return $"Alerts {alerts}; duty pops {duty}; remote chat {chat}; party {s.Party.Count}/{s.Target} ({DescribeSource(s.Party)}); {DescribeState(s.State)}.{setup}";
    }
}
