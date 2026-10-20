using System;
using Dalamud.Configuration;
using PfNotification.Ntfy;

namespace PfNotification;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public const string DefaultServerUrl = "https://ntfy.shifusenproductions.com";
    public const string ChatTopicSuffix = "-chat";
    public const int MinTargetSize = 2;
    public const int MaxTargetSize = 8;

    public int Version { get; set; } = 1;

    /// <summary>Send alerts. Detection keeps running when off, so switching on with a full party doesn't fire.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>ntfy server base URL. Must be https://.</summary>
    public string ServerUrl { get; set; } = DefaultServerUrl;

    /// <summary>ntfy topic this player subscribes to on their phone, e.g. "pf-alerts".</summary>
    public string Topic { get; set; } = string.Empty;

    /// <summary>ntfy access token (tk_…). Stored UNENCRYPTED in the plugin config file; this is a private plugin.</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>ntfy priority, 1 (min) to 5 (max). 4 ("high") vibrates and pops up on most phones.</summary>
    public int Priority { get; set; } = 4;

    /// <summary>Also notify when a member joins or leaves (outside duties), e.g. "WHM joined".</summary>
    public bool JoinLeaveAlerts { get; set; } = true;

    /// <summary>ntfy priority for join/leave alerts. 2 ("low") shows them without sound or vibration.</summary>
    public int JoinLeavePriority { get; set; } = 2;

    /// <summary>Notify when a Duty Finder queue or roulette pops.</summary>
    public bool DutyPopAlerts { get; set; } = true;

    /// <summary>ntfy priority for duty pops. 5 ("max") gets through Do Not Disturb on most phones.</summary>
    public int DutyPopPriority { get; set; } = 5;

    /// <summary>Party size that counts as full: 8 for a full party, 4 for a light party.</summary>
    public int TargetSize { get; set; } = 8;

    /// <summary>Also print a chat line (only in your own chat log) after each alert.</summary>
    public bool ChatConfirmation { get; set; } = true;

    /// <summary>Log party, condition and system-message details to /xllog, to tune detection.</summary>
    public bool DiagnosticLogging { get; set; } = false;

    /// <summary>
    /// Post messages published to the chat topic to party chat. Off by default: this is the one feature that makes the
    /// client send something to the game server. See the passive-only rule in docs/dalamud-api.md.
    /// </summary>
    public bool RemoteChatEnabled { get; set; } = false;

    /// <summary>Topic the phone publishes chat to. Empty means <see cref="Topic"/> plus <see cref="ChatTopicSuffix"/>.</summary>
    public string RemoteChatTopic { get; set; } = string.Empty;

    // Methods rather than properties: Newtonsoft would write get-only properties into the config file.
    public int GetTargetSize() => Math.Clamp(TargetSize, MinTargetSize, MaxTargetSize);

    public NtfySettings ToNtfySettings() => new(ServerUrl.Trim(), Topic.Trim(), Token.Trim());

    /// <summary>Why alerts can't be sent with the current settings, or null when they can.</summary>
    public string? GetConfigProblem() => NtfyClient.Validate(ToNtfySettings());

    /// <summary>The chat topic in use: the explicit one, or the alerts topic with <see cref="ChatTopicSuffix"/>.</summary>
    public string GetRemoteChatTopic()
    {
        var explicitTopic = RemoteChatTopic.Trim();
        if (explicitTopic.Length > 0)
        {
            return explicitTopic;
        }

        var alerts = Topic.Trim();
        return (alerts.Length > 0) ? (alerts + ChatTopicSuffix) : string.Empty;
    }

    /// <summary>Same server and token as the alerts; the token needs read access to the chat topic on the server.</summary>
    public NtfySettings ToRemoteChatSettings() => new(ServerUrl.Trim(), GetRemoteChatTopic(), Token.Trim());

    /// <summary>Why remote chat can't run with the current settings, or null when it can.</summary>
    public string? GetRemoteChatProblem()
    {
        var settings = ToRemoteChatSettings();
        if (NtfyClient.Validate(settings) is { } problem)
        {
            return (settings.Topic.Length == 0) ? "chat topic is empty (set the alerts topic or a chat topic)" : problem;
        }

        // Reading the alerts topic back would post the plugin's own alerts and confirmations to party chat.
        if (string.Equals(settings.Topic, Topic.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return "chat topic must differ from the alerts topic";
        }

        return null;
    }

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
