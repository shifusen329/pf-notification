using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

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
        Size = new Vector2(480, 440) * ImGuiHelpers.GlobalScale;
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

        var diagnostics = configuration.DiagnosticLogging;
        if (ImGui.Checkbox("Diagnostic logging (/xllog)", ref diagnostics))
        {
            plugin.SetDiagnostics(diagnostics);
        }

        ImGui.Spacing();
        ImGui.TextDisabled("Read-only: watches your own client and never sends anything to the game servers.");
    }
}
