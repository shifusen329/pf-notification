using System;
using Dalamud.Configuration;
using PfNotification.Ntfy;

namespace PfNotification;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public const string DefaultServerUrl = "https://ntfy.shifusenproductions.com";
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

    /// <summary>Party size that counts as full: 8 for a full party, 4 for a light party.</summary>
    public int TargetSize { get; set; } = 8;

    /// <summary>Also print a chat line (only in your own chat log) after each alert.</summary>
    public bool ChatConfirmation { get; set; } = true;

    /// <summary>Log party, condition and system-message details to /xllog, to tune detection.</summary>
    public bool DiagnosticLogging { get; set; } = false;

    // Methods rather than properties: Newtonsoft would write get-only properties into the config file.
    public int GetTargetSize() => Math.Clamp(TargetSize, MinTargetSize, MaxTargetSize);

    public NtfySettings ToNtfySettings() => new(ServerUrl.Trim(), Topic.Trim(), Token.Trim());

    /// <summary>Why alerts can't be sent with the current settings, or null when they can.</summary>
    public string? GetConfigProblem() => NtfyClient.Validate(ToNtfySettings());

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
