using System;
using System.Collections.Generic;
using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using PfNotification.Party;

namespace PfNotification.Diagnostics;

/// <summary>
/// Optional /xllog output for tuning detection: party snapshot changes, the condition flags that matter, and every
/// system log message (to find the "recruitment complete" <c>LogMessageId</c>). Subscribes only while enabled.
/// <para>
/// Passive-only: observes events, never modifies or suppresses them. Deliberately does NOT call
/// <c>ILogMessage.FormatLogMessageForDebugging()</c>, which Dalamud documents as able to replay side effects such as
/// sound effects; it logs the raw LogMessage sheet text and integer parameters instead.
/// </para>
/// </summary>
public sealed class DiagnosticLogger : IDisposable
{
    private static readonly HashSet<ConditionFlag> WatchedFlags =
    [
        ConditionFlag.UsingPartyFinder,
        ConditionFlag.ParticipatingInCrossWorldPartyOrAlliance,
        ConditionFlag.BoundByDuty,
        ConditionFlag.BoundByDuty56,
        ConditionFlag.BoundByDuty95,
        ConditionFlag.BetweenAreas,
        ConditionFlag.BetweenAreas51,
    ];

    private static readonly ConditionFlag[] DutyFlags =
    [
        ConditionFlag.BoundByDuty,
        ConditionFlag.BoundByDuty56,
        ConditionFlag.BoundByDuty95,
    ];

    private readonly IChatGui chatGui;
    private readonly ICondition condition;
    private readonly IPluginLog log;

    public DiagnosticLogger(IChatGui chatGui, ICondition condition, IPluginLog log)
    {
        this.chatGui = chatGui;
        this.condition = condition;
        this.log = log;
    }

    public bool Enabled { get; private set; }

    public void SetEnabled(bool enabled)
    {
        if (enabled == Enabled)
        {
            return;
        }

        if (enabled)
        {
            chatGui.LogMessage += OnLogMessage;
            condition.ConditionChange += OnConditionChange;
        }
        else
        {
            chatGui.LogMessage -= OnLogMessage;
            condition.ConditionChange -= OnConditionChange;
        }

        Enabled = enabled;
        log.Information("[diag] diagnostic logging {State}", enabled ? "on" : "off");
    }

    public void OnPartyChanged(PartySnapshot before, PartySnapshot after, bool frozen)
    {
        if (!Enabled)
        {
            return;
        }

        log.Information(
            "[diag] party {BeforeCount} ({BeforeSource}) -> {AfterCount} ({AfterSource}{SelfAdded}){Frozen}",
            before.Count,
            before.Source,
            after.Count,
            after.Source,
            after.SelfAdded ? ", self added" : string.Empty,
            frozen ? " [frozen]" : string.Empty);
    }

    public void Dispose()
    {
        SetEnabled(false);
    }

    private void OnLogMessage(ILogMessage message)
    {
        // Combat floods the log inside duties, and nothing there is about Party Finder recruitment.
        if (condition.Any(DutyFlags))
        {
            return;
        }

        // Parameters are only valid during this event, so copy everything now.
        var id = message.LogMessageId;
        var text = message.GameData.IsValid ? message.GameData.Value.Text.ExtractText() : "?";
        var ints = new List<int>(message.ParameterCount);
        for (var i = 0; i < message.ParameterCount; i++)
        {
            if (message.TryGetIntParameter(i, out var value))
            {
                ints.Add(value);
            }
        }

        log.Information("[diag] LogMessage {Id}: \"{Text}\" ints=[{Ints}]", id, text, string.Join(", ", ints));
    }

    private void OnConditionChange(ConditionFlag flag, bool value)
    {
        if (WatchedFlags.Contains(flag))
        {
            log.Information("[diag] condition {Flag} = {Value}", flag, value);
        }
    }
}
