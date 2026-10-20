using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using PfNotification.Ntfy;
using PfNotification.RemoteChat;

namespace PfNotification.Windows;

/// <summary>Status and settings in one window. Reads only the plugin's published records, never game state.</summary>
public sealed class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly Configuration configuration;

    private bool showToken;

    public MainWindow(Plugin plugin) : base("PF Notification###PfNotification")
    {
        this.plugin = plugin;
        configuration = plugin.Configuration;
        Flags = ImGuiWindowFlags.NoCollapse;
        Size = new Vector2(500, 700) * ImGuiHelpers.GlobalScale;
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose() { }

    public override void Draw()
    {
        DrawStatus();
        Section();
        DrawDelivery();
        Section();
        DrawDetection();
        Section();
        DrawRemoteChat();
        ImGui.Spacing();
        ImGui.TextDisabled("Passive by default: watches your own client and never sends anything to the game servers.");
        ImGui.TextDisabled("Remote party chat is the one exception, and is off unless you enable it above.");
    }

    private static void Section()
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
    }

    private void DrawStatus()
    {
        var status = plugin.Status;
        if (status is null)
        {
            ImGui.TextDisabled("Waiting for the game…");
        }
        else
        {
            ImGui.TextUnformatted($"Party: {status.Party.Count}/{status.Target} ({Plugin.DescribeSource(status.Party)})");
            ImGui.TextUnformatted($"State: {Plugin.DescribeState(status.State)}");
        }

        if (!configuration.Enabled)
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, "Alerts are off. Tick \"Send alerts\" or use /pfnotify on.");
        }

        if (configuration.GetConfigProblem() is { } problem)
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, $"Not configured: {problem}.");
        }

        if (plugin.LastSend is { } last)
        {
            ImGui.TextUnformatted($"Last send ({Plugin.Describe(last.Kind).ToLowerInvariant()}) at {last.At:HH:mm:ss}:");
            ImGui.SameLine();
            if (last.Result.Ok)
            {
                ImGui.TextColored(ImGuiColors.HealerGreen, "sent");
            }
            else
            {
                ImGui.TextColored(ImGuiColors.DalamudRed, $"{last.Result.Outcome}: {last.Result.Detail}");
            }
        }
    }

    private void DrawDelivery()
    {
        ImGui.TextUnformatted("Delivery (ntfy)");
        ImGui.SameLine();
        ImGuiComponents.HelpMarker(
            "Alerts are published to your topic on an ntfy server. Subscribe to the same topic in the ntfy app on your phone.\n" +
            "The access token is stored UNENCRYPTED in the plugin config file.");

        var enabled = configuration.Enabled;
        if (ImGui.Checkbox("Send alerts", ref enabled))
        {
            configuration.Enabled = enabled;
            configuration.Save();
        }

        var server = configuration.ServerUrl;
        if (ImGui.InputText("Server URL", ref server, 256))
        {
            configuration.ServerUrl = server.Trim();
            configuration.Save();
        }

        var topic = configuration.Topic;
        if (ImGui.InputText("Topic", ref topic, 64))
        {
            configuration.Topic = topic.Trim();
            configuration.Save();
        }

        var token = configuration.Token;
        var tokenFlags = showToken ? ImGuiInputTextFlags.None : ImGuiInputTextFlags.Password;
        if (ImGui.InputText("Access token", ref token, 128, tokenFlags))
        {
            configuration.Token = token.Trim();
            configuration.Save();
        }

        ImGui.SameLine();
        if (ImGuiComponents.IconButton("##toggleToken", showToken ? FontAwesomeIcon.EyeSlash : FontAwesomeIcon.Eye))
        {
            showToken = !showToken;
        }

        var priority = configuration.Priority;
        if (ImGui.SliderInt("Party-full priority", ref priority, 1, 5))
        {
            configuration.Priority = priority;
            configuration.Save();
        }

        ImGui.SameLine();
        ImGuiComponents.HelpMarker("1 min, 2 low, 3 default, 4 high (pops up), 5 max (urgent).");

        var chat = configuration.ChatConfirmation;
        if (ImGui.Checkbox("Confirm in chat after each alert", ref chat))
        {
            configuration.ChatConfirmation = chat;
            configuration.Save();
        }

        using (ImRaii.Disabled(plugin.IsTestRunning || (configuration.GetConfigProblem() is not null)))
        {
            if (ImGui.Button("Send test notification"))
            {
                plugin.SendTest();
            }
        }

        if (plugin.IsTestRunning)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("sending…");
        }
    }

    private void DrawDetection()
    {
        ImGui.TextUnformatted("Detection");

        var target = configuration.TargetSize;
        if (ImGui.SliderInt("Full at", ref target, Configuration.MinTargetSize, Configuration.MaxTargetSize))
        {
            configuration.TargetSize = target;
            configuration.Save();
        }

        ImGui.SameLine();
        ImGuiComponents.HelpMarker("8 for a full party, 4 for a light party. Also /pfnotify target <n>.");

        var joinLeave = configuration.JoinLeaveAlerts;
        if (ImGui.Checkbox("Notify when members join or leave", ref joinLeave))
        {
            configuration.JoinLeaveAlerts = joinLeave;
            configuration.Save();
        }

        ImGui.SameLine();
        ImGuiComponents.HelpMarker(
            "e.g. \"WHM joined\" with the new count and roles. Jobs only, never names.\n" +
            "Not sent in duties, or when you join or leave a party yourself. Also /pfnotify joins on|off.");

        using (ImRaii.Disabled(!configuration.JoinLeaveAlerts))
        {
            var joinLeavePriority = configuration.JoinLeavePriority;
            if (ImGui.SliderInt("Join/leave priority", ref joinLeavePriority, 1, 5))
            {
                configuration.JoinLeavePriority = joinLeavePriority;
                configuration.Save();
            }
        }

        ImGui.SameLine();
        ImGuiComponents.HelpMarker("2 (low) shows them without sound or vibration; the party-full alert keeps its own priority.");

        var dutyPop = configuration.DutyPopAlerts;
        if (ImGui.Checkbox("Notify when a duty pops", ref dutyPop))
        {
            configuration.DutyPopAlerts = dutyPop;
            configuration.Save();
        }

        ImGui.SameLine();
        ImGuiComponents.HelpMarker(
            "Duty Finder and roulettes: \"Duty ready\" with the duty's name, so you can get back within the 45 s accept window.\n" +
            "Each re-pop after someone declines alerts again. Also /pfnotify duty on|off.");

        using (ImRaii.Disabled(!configuration.DutyPopAlerts))
        {
            var dutyPriority = configuration.DutyPopPriority;
            if (ImGui.SliderInt("Duty-ready priority", ref dutyPriority, 1, 5))
            {
                configuration.DutyPopPriority = dutyPriority;
                configuration.Save();
            }
        }

        ImGui.SameLine();
        ImGuiComponents.HelpMarker("5 (max) is urgent: it gets through Do Not Disturb on most phones.");

        var diagnostics = configuration.DiagnosticLogging;
        if (ImGui.Checkbox("Diagnostic logging (/xllog)", ref diagnostics))
        {
            plugin.SetDiagnostics(diagnostics);
        }
    }

    private void DrawRemoteChat()
    {
        ImGui.TextUnformatted("Remote party chat (phone → game)");
        ImGui.SameLine();
        ImGuiComponents.HelpMarker(
            "Messages you publish to the chat topic are posted to party chat as \"/p <text>\" by your client.\n" +
            "Server side, your plugin token needs read access to that topic (see README). Send from the ntfy web app:\n" +
            "open the server in your phone's browser, log in, open the chat topic and type in the box at the bottom.\n" +
            "One line, no commands, 500 bytes max, at most one message every 2 s and 10 a minute. Messages older than\n" +
            "60 s are dropped, so nothing typed while the game was closed is ever posted. Also /pfnotify chat on|off.");

        ImGui.TextColored(
            ImGuiColors.DalamudYellow,
            "Not passive: when on, anything published to the chat topic is spoken by your character in party chat.");
        ImGui.TextColored(ImGuiColors.DalamudYellow, "Anyone who can publish to that topic can speak as you.");

        var problem = configuration.GetRemoteChatProblem();
        using (ImRaii.Disabled((problem is not null) && !configuration.RemoteChatEnabled))
        {
            var enabled = configuration.RemoteChatEnabled;
            if (ImGui.Checkbox("Enable remote party chat", ref enabled))
            {
                configuration.RemoteChatEnabled = enabled;
                configuration.Save();
            }
        }

        var chatTopic = configuration.RemoteChatTopic;
        var alerts = configuration.Topic.Trim();
        var hint = ((alerts.Length > 0) ? alerts : "pf-alerts") + Configuration.ChatTopicSuffix;
        if (ImGui.InputTextWithHint("Chat topic", hint, ref chatTopic, 64))
        {
            configuration.RemoteChatTopic = chatTopic.Trim();
            configuration.Save();
        }

        ImGui.SameLine();
        ImGuiComponents.HelpMarker("Leave empty to use the alerts topic with \"-chat\" added. It must not be the alerts topic itself.");

        if (problem is not null)
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, $"Can't start: {problem}.");
        }

        ImGui.TextUnformatted("Stream:");
        ImGui.SameLine();
        if (!configuration.RemoteChatEnabled)
        {
            ImGui.TextDisabled("off");
        }
        else
        {
            var status = plugin.RemoteChatStatus;
            var colour = status?.State switch
            {
                SubscriberState.Connected => ImGuiColors.HealerGreen,
                SubscriberState.AuthRejected or SubscriberState.Forbidden or SubscriberState.Failed => ImGuiColors.DalamudRed,
                null or SubscriberState.Stopped or SubscriberState.Connecting => ImGuiColors.DalamudGrey,
                _ => ImGuiColors.DalamudYellow,
            };
            ImGui.TextColored(colour, RemoteChatService.Describe(status));
        }

        if (plugin.LastRemoteChat is { } last)
        {
            var outcome = (last.Verdict == RemoteChatVerdict.Accept) ? "sent" : $"rejected ({last.Reason})";
            ImGui.TextUnformatted($"Last message at {last.At:HH:mm:ss}: {outcome}: {last.Text}");
        }
    }
}
