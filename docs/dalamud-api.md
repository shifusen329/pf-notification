# Dalamud API reference for PF Notification

The subset of the Dalamud plugin API this plugin needs, mapped to what the plugin has to do. It's the starting point for replacing the SamplePlugin template code.

* **Source:** https://dalamud.dev/api/ (namespace index), plus the release notes at https://dalamud.dev/versions/v15, https://dalamud.dev/versions/v14 and https://dalamud.dev/versions/v13
* **Reviewed:** 2026-10-01
* **API version covered:** the docs at `/api/` are labelled **"15.x (API 15) [Current]"**. The v15 release notes say API 15 corresponds to **Dalamud.NET.Sdk v15.0.0** (released with Patch 7.5 on 29/04/2026), so this matches the project's `Dalamud.NET.Sdk/15.0.0`. v16 (API 16, for Patch 8.0) is documented as "not finalized" with release date "TBA".
* **Conventions:** everything below was read in the docs unless marked **(unverified)**, which means it's an inference or community knowledge. Members are listed with the signatures the docs show. Where a page shows no signature (for example constructors), that is said explicitly.

## Passive-only rule

The plugin must be invisible to Square Enix's servers: it observes state the client has already received and never sends anything to the game server or changes what the client does.

**Allowed (read-only, local):**
* Subscribing to `IChatGui.LogMessage` / `ChatMessage` and reading the message. Never call `PreventOriginal()` and never modify the message.
* Reading `IPartyList`, `ICondition`, `IClientState` and `IPlayerState` from `IFramework.Update` or events.
* Reading `IPartyFinderGui.ReceiveListing` data when the player opens the Party Finder themselves. Never set `args.Visible`, which hides listings.
* Reading FFXIVClientStructs fields, if the cross-world path needs them.
* Local feedback: `IChatGui.Print` / `PrintError` (written to your own chat log only), `INotificationManager`, `IToastGui`, `IPluginLog`.
* The plugin's own slash command. Dalamud handles it locally.
* The HTTPS POST to ntfy. It goes to your server; nothing goes to Square Enix.

**Not allowed:**
* Packet-level hooks on the game's network functions. They're unnecessary, because everything above is the client's already-decoded state, and opcodes change every patch.
* Anything that makes the client contact the server: sending chat text or game commands (`ICommandManager.ProcessCommand` with a game command), opening or refreshing the Party Finder, interacting with agents or addons, or calling FFXIVClientStructs functions that request data or change client state. `InfoProxyCrossRealm` has `RequestData()`, `EndRequest()`, `AddData()`, `ClearListData()` and `HandleZoneInitPacket()`. Read its fields; never call those.
* `ILogMessage.FormatLogMessageForDebugging()`. Its docs warn: "This can cause side effects such as playing sound effects and thus should only be used for debugging." Log `LogMessageId`, the raw sheet text (`GameData.Value.Text`) and the int parameters instead.
* Automating any player action.

Passive behaviour removes the server-visible signals, not the risk entirely: Dalamud runs inside the game process, and third-party tools are against the game's terms regardless of what they do.

## Summary

| Namespace | Types / services used | Why the plugin needs it |
|---|---|---|
| [`Dalamud.Plugin`](https://dalamud.dev/api/Dalamud.Plugin/) | `IDalamudPlugin`, `IDalamudPluginInterface` | Plugin entry point; config save/load; access to `UiBuilder` |
| [`Dalamud.IoC`](https://dalamud.dev/api/Dalamud.IoC/) | `PluginServiceAttribute` | `[PluginService]` injection of every service below |
| [`Dalamud.Plugin.Services`](https://dalamud.dev/api/Dalamud.Plugin.Services/) | `IPartyList`, `IPartyFinderGui`, `IChatGui`, `IFramework`, `IClientState`, `IPlayerState`, `ICondition`, `ICommandManager`, `IPluginLog`, `INotificationManager`, `IToastGui` (optional: `IObjectTable`, `IAgentLifecycle`) | Party detection, per-frame logic, commands, in-game feedback |
| [`Dalamud.Game.ClientState.Party`](https://dalamud.dev/api/Dalamud.Game.ClientState.Party/) | `IPartyMember` | Elements of `IPartyList` |
| [`Dalamud.Game.Gui.PartyFinder.Types`](https://dalamud.dev/api/Dalamud.Game.Gui.PartyFinder.Types/) | `IPartyFinderListing`, `IPartyFinderListingEventArgs` | Listing data (slot counts) from `IPartyFinderGui.ReceiveListing` |
| [`Dalamud.Game.Chat`](https://dalamud.dev/api/Dalamud.Game.Chat/) | `IChatMessage`, `IHandleableChatMessage`, `ILogMessage` | Event payloads for detecting the "recruitment complete" message |
| [`Dalamud.Game.Text`](https://dalamud.dev/api/Dalamud.Game.Text/) | `XivChatType`, `XivChatEntry` | Filtering chat by type; printing chat |
| [`Dalamud.Game.ClientState.Conditions`](https://dalamud.dev/api/Dalamud.Game.ClientState.Conditions/) | `ConditionFlag` | `UsingPartyFinder`, `ParticipatingInCrossWorldPartyOrAlliance` |
| [`Dalamud.Game.Command`](https://dalamud.dev/api/Dalamud.Game.Command/) | `CommandInfo`, `IReadOnlyCommandInfo.HandlerDelegate` | Registering the slash command |
| [`Dalamud.Configuration`](https://dalamud.dev/api/Dalamud.Configuration/) | `IPluginConfiguration` | Persisted settings: URL, topic, token, priority |
| [`Dalamud.Interface`](https://dalamud.dev/api/Dalamud.Interface/) | `IUiBuilder` | `Draw` / `OpenConfigUi` / `OpenMainUi` events |
| [`Dalamud.Interface.Windowing`](https://dalamud.dev/api/Dalamud.Interface.Windowing/) | `WindowSystem`, `Window` | Settings window |
| `Dalamud.Bindings.ImGui` (not in the API index) | `ImGui`, `ImGuiWindowFlags`, `ImGuiCond` | Widgets in the settings window |
| [`Dalamud.Interface.ImGuiNotification`](https://dalamud.dev/api/Dalamud.Interface.ImGuiNotification/) | `Notification`, `NotificationType`, `IActiveNotification` | Dalamud toast-style notification for POST success/failure |
| [`Dalamud.Game.Gui.Toast`](https://dalamud.dev/api/Dalamud.Game.Gui.Toast/) | `ToastOptions` | Native game toast (alternative feedback) |
| [`Dalamud.Networking.Http`](https://dalamud.dev/api/Dalamud.Networking.Http/) | `HappyEyeballsCallback` (optional) | Optional connect callback for the plugin's own `HttpClient` |

The template also injects `ITextureProvider` and `IDataManager` and uses `Lumina.Excel.Sheets` for the goat image and the sample main window. This plugin doesn't need them once those are removed.

---

## 1. Detect that the party is full

### Option A: party size, using `IPartyList`

**[`IPartyList`](https://dalamud.dev/api/Dalamud.Plugin.Services/Interfaces/IPartyList)** in `Dalamud.Plugin.Services`. Injected with `[PluginService]`. Doc summary: "This collection represents the actors present in your party or alliance." Declared as `IPartyList : IDalamudService, IReadOnlyCollection<IPartyMember>, IEnumerable<IPartyMember>`, so it can be enumerated directly.

| Member | Description (from docs) |
|---|---|
| `int Length` | "Gets the amount of party members the local player has." Whether this counts the local player is **(unverified)**. |
| `IPartyMember? this[int index]` | "Get a party member at the specified spawn index." |
| `uint PartyLeaderIndex` | Index of the party leader. |
| `bool IsAlliance` | Whether the group is an alliance. |
| `long PartyId` | ID of the party. |
| `nint GroupManagerAddress`, `GroupListAddress`, `AllianceListAddress` | Raw addresses. `GroupManagerAddress` is "the address of the Group Manager". |
| `nint GetPartyMemberAddress(int index)` / `IPartyMember? CreatePartyMemberReference(nint address)` | Raw access to party members. |
| `nint GetAllianceMemberAddress(int index)` / `IPartyMember? CreateAllianceMemberReference(nint address)` | Raw access to alliance members. |

**[`IPartyMember`](https://dalamud.dev/api/Dalamud.Game.ClientState.Party/Interfaces/IPartyMember)** in `Dalamud.Game.ClientState.Party`. Not injected; you get it from `IPartyList`. Useful members: `ulong ContentId`, `uint EntityId`, `SeString Name`, `RowRef<World> World` ("the World this party member resides in"), `RowRef<ClassJob> ClassJob`, `byte Level`. `ObjectId` is marked obsolete; use `EntityId`.

**No party-changed event** is documented on `IPartyList`. Poll `Length` from `IFramework.Update` (see §2) and compare it with the expected size.

**Cross-world fallback, as used by PartyVet** (`../PartyVet/PartyVet/Party/PartySnapshot.cs`). `IPartyList` covers same-world parties, and any party once inside a duty. A Party Finder or cross-world party *before entering the duty* isn't in it (`Length == 0`) and lives in FFXIVClientStructs' `InfoProxyCrossRealm` instead. PartyVet reads it like this, on the framework thread:

```csharp
using FFXIVClientStructs.FFXIV.Client.UI.Info;

var proxy = InfoProxyCrossRealm.Instance();
if (proxy != null && proxy->IsCrossRealm && proxy->GroupCount > 0)
{
    ref var group = ref proxy->CrossRealmGroups[0];
    var count = group.GroupMemberCount;   // members: group.GroupMembers[i].NameString, .HomeWorld, .ClassJobId
}
```

It needs `unsafe` and only reads fields, which keeps it passive. The SDK references Dalamud's bundled FFXIVClientStructs by default. PartyVet also notes that the local player appears in `IPartyList` when it's populated. Whether the cross-realm group includes the local player is **(unverified)**.

**Where the slot count comes from:** `IPartyList` has no notion of a listing. The expected size comes either from your own listing (option C, if it's received) or from a user setting, such as 8 for a full party **(unverified design choice)**.

### Option B: the system/log message when recruitment completes, using `IChatGui`

**[`IChatGui`](https://dalamud.dev/api/Dalamud.Plugin.Services/Interfaces/IChatGui)** in `Dalamud.Plugin.Services`. Injected with `[PluginService]`. "This class handles interacting with the native chat UI."

| Event | Delegate signature | Description (from docs) |
|---|---|---|
| `ChatMessage` | [`OnHandleableChatMessageDelegate`](https://dalamud.dev/api/Dalamud.Plugin.Services/Delegates/IChatGui.OnHandleableChatMessageDelegate): `void (IHandleableChatMessage message)` | "fired when a chat message is sent to chat by the game." |
| `CheckMessageHandled` | same | Follow-up to `ChatMessage` for final modifications; only fired if not suppressed. |
| `ChatMessageHandled` / `ChatMessageUnhandled` | [`OnChatMessageDelegate`](https://dalamud.dev/api/Dalamud.Plugin.Services/Delegates/IChatGui.OnChatMessageDelegate): `void (IChatMessage message)` | Fired after a message was or wasn't handled by Dalamud or a plugin. |
| `LogMessage` | [`OnLogMessageDelegate`](https://dalamud.dev/api/Dalamud.Plugin.Services/Delegates/IChatGui.OnLogMessageDelegate): `void (ILogMessage message)` | "fired when a log message, that is a chat message based on entries in the LogMessage sheet, is sent." |

The v15 release notes say: "Remember to try out the `LogMessage` event introduced in API 14, which may be a suitable alternative for intercepting system messages." For this plugin, `LogMessage` is the better hook, because matching a numeric `LogMessageId` doesn't depend on the client language. Matching message text does **(unverified design choice)**.

**[`IChatMessage`](https://dalamud.dev/api/Dalamud.Game.Chat/Interfaces/IChatMessage)** in `Dalamud.Game.Chat`. Properties: `XivChatType LogKind` ("the type of chat"), `SeString Sender`, `SeString Message`, `ReadOnlySeString OriginalSender` / `OriginalMessage` (the values before any plugin changed them), `int Timestamp`, `bool IsHandled`, `XivChatRelationKind SourceKind` / `TargetKind`.
**[`IHandleableChatMessage`](https://dalamud.dev/api/Dalamud.Game.Chat/Interfaces/IHandleableChatMessage)** extends `IMutableChatMessage` and `IChatMessage`, and adds `void PreventOriginal()`. Don't call it here: the plugin should only observe.

**[`ILogMessage`](https://dalamud.dev/api/Dalamud.Game.Chat/Interfaces/ILogMessage)** in `Dalamud.Game.Chat`.

| Member | Description (from docs) |
|---|---|
| `uint LogMessageId` | "Gets the ID of this log message." |
| `RowRef<LogMessage> GameData` | The LogMessage sheet row. |
| `int ParameterCount`, `IReadOnlyList<SeStringParameter> Parameters` | Only valid during the event; "must not be accessed after returning from it." |
| `bool TryGetIntParameter(int, out int)` / `TryGetStringParameter(int, out ReadOnlySeString)` | Read parameters. |
| `FormatLogMessageForDebugging()` | Approximates the final log string. **Don't use it:** the docs warn it can replay side effects such as sound effects. Use `GameData.Value.Text` plus the parameters. |
| `ILogMessageEntity? SourceEntity` / `TargetEntity`, `bool IsHandled`, `PreventOriginal()` | Not needed. |

**[`XivChatType`](https://dalamud.dev/api/Dalamud.Game.Text/Enums/XivChatType)** in `Dalamud.Game.Text` ("as seen in the LogKind excel sheet"). Candidates for the PF message: `SystemMessage = 57`, `Notice = 3`, `Urgent = 2`, and `PeriodicRecruitmentNotification = 72` ("The periodic recruitment notification chat type."). It's **unverified** which chat type, or which `LogMessageId`, the "party recruitment complete" message uses. The plugin's diagnostic logging records `LogMessageId`, the raw sheet text and the int parameters, which is enough to find it during a real fill.

### Option C: Party Finder specific API, using `IPartyFinderGui`

**[`IPartyFinderGui`](https://dalamud.dev/api/Dalamud.Plugin.Services/Interfaces/IPartyFinderGui)** in `Dalamud.Plugin.Services`. Injected with `[PluginService]`. "This class handles interacting with the native PartyFinder window." Its only member is the event `ReceiveListing`: "Event fired each time the game receives an individual Party Finder listing. Cannot modify listings but can hide them."
Delegate: [`PartyFinderListingEventDelegate`](https://dalamud.dev/api/Dalamud.Plugin.Services/Delegates/IPartyFinderGui.PartyFinderListingEventDelegate), `void (IPartyFinderListing listing, IPartyFinderListingEventArgs args)`.

**[`IPartyFinderListing`](https://dalamud.dev/api/Dalamud.Game.Gui.PartyFinder.Types/Interfaces/IPartyFinderListing)** in `Dalamud.Game.Gui.PartyFinder.Types`. Relevant properties:

| Property | Description (from docs) |
|---|---|
| `byte SlotsAvailable` | "the number of player slots this listing is recruiting for." |
| `byte SlotsFilled` | "the number of player slots filled." |
| `byte Parties` | "the number of parties this listing is recruiting for." |
| `IReadOnlyCollection<PartyFinderSlot> Slots` | Slots and the jobs each accepts ([`PartyFinderSlot`](https://dalamud.dev/api/Dalamud.Game.Gui.PartyFinder.Types/Classes/PartyFinderSlot)). |
| `ulong ContentId` | "the player's unique content ID" (the host). |
| `ulong Id` | Server-assigned listing ID. |
| `SeString Name` | Host name. |
| `RowRef<World> World` / `HomeWorld` / `CurrentWorld` | Listing world, host's home world, host's current world. |
| `ushort SecondsRemaining` | "It may end before this time if the party fills or the host ends it early." |
| `SearchAreaFlags SearchArea` | Also denotes alliance raid and one-player-per-job listings. |

[`IPartyFinderListingEventArgs`](https://dalamud.dev/api/Dalamud.Game.Gui.PartyFinder.Types/Interfaces/IPartyFinderListingEventArgs): `int BatchNumber`, `bool Visible { get; set; }`.

**Own listing or browsing only?** The docs don't say. The wording ("each time the game receives an individual Party Finder listing", "can hide them") points to the listing feed you get while **browsing** the Party Finder window. It is **unverified** whether your own listing arrives through this event, and it wouldn't arrive at all while the PF window is closed (**unverified**). If it does, you could find it by comparing `listing.ContentId` with `IPlayerState.ContentId` and read `SlotsFilled`/`SlotsAvailable`. Don't rely on this as the main trigger.

**Other PF-related items in the docs:**

* [`ConditionFlag`](https://dalamud.dev/api/Dalamud.Game.ClientState.Conditions/Enums/ConditionFlag): `UsingPartyFinder = 66` ("Unable to execute command while using the Party Finder.") and `ParticipatingInCrossWorldPartyOrAlliance = 84`. Whether `UsingPartyFinder` stays set for the whole time your listing is active, and clears when it fills, is **unverified**.
* [`AgentId`](https://dalamud.dev/api/Dalamud.Game.Agent/Enums/AgentId) has `LookingForGroup = 130`, and [`IAgentLifecycle`](https://dalamud.dev/api/Dalamud.Plugin.Services/Interfaces/IAgentLifecycle) has `RegisterListener(AgentEvent, AgentId, AgentEventDelegate)` with [`AgentEvent`](https://dalamud.dev/api/Dalamud.Game.Agent/Enums/AgentEvent) values such as `PostReceiveEvent` and `PostUpdate`. This is a lead only. Reading the agent's data would need FFXIVClientStructs, and whether it exposes your own recruitment state is **unverified**.

---

## 2. Run logic each frame or on events

**[`IFramework`](https://dalamud.dev/api/Dalamud.Plugin.Services/Interfaces/IFramework)** in `Dalamud.Plugin.Services`. Injected with `[PluginService]`.

| Member | Description (from docs) |
|---|---|
| `event OnUpdateDelegate Update` | "Event that gets fired every time the game framework updates." Delegate: [`void (IFramework framework)`](https://dalamud.dev/api/Dalamud.Plugin.Services/Delegates/IFramework.OnUpdateDelegate). |
| `bool IsInFrameworkUpdateThread` | Whether the current code runs on the framework update thread. |
| `TimeSpan UpdateDelta`, `DateTime LastUpdate` / `LastUpdateUTC` | Timing, useful for throttling the poll. |
| `bool IsFrameworkUnloading` | Game framework is unloading. |
| `Task RunOnFrameworkThread(Action action)` / `Task<T> RunOnFrameworkThread<T>(Func<T> func)` | Run on the framework thread. Per the docs, `await` continues off the framework thread. |
| `Task Run(Action action, CancellationToken cancellationToken = default)` (and `Func<T>`, `Func<Task>`, `Func<Task<T>>` overloads) | Runs immediately if already on the framework thread, otherwise on the next update. |
| `Task RunOnTick(Action action, TimeSpan delay = default, int delayTicks = 0, CancellationToken cancellationToken = default)` (and overloads) | Run on an upcoming tick, with an optional delay. |
| `Task DelayTicks(long numTicks, CancellationToken cancellationToken = default)`, `IDebouncer CreateDebouncer(TimeSpan delay, Action action)`, `TaskFactory GetTaskFactory()` | Helpers. |

**[`IClientState`](https://dalamud.dev/api/Dalamud.Plugin.Services/Interfaces/IClientState)** in `Dalamud.Plugin.Services`. Injected with `[PluginService]`.

* Events: `event Action Login` ("fires when a character is logging in, and the local character object is available"), `event IClientState.LogoutDelegate Logout` (delegate [`void (int type, int code)`](https://dalamud.dev/api/Dalamud.Plugin.Services/Delegates/IClientState.LogoutDelegate)), `event Action<uint> TerritoryChanged`, `event Action<ContentFinderCondition> CfPop`, `ZoneInit`, `MapIdChanged`, `InstanceChanged`, `EnterPvP` / `LeavePvP`, `ClassJobChanged`, `LevelChanged`.
* Properties: `bool IsLoggedIn`, `uint TerritoryType`, `uint MapId`, `uint Instance`, `ClientLanguage ClientLanguage`, `IsPvP`, `IsGPosing`.
* Methods: `IsClientIdle()` / `IsClientIdle(out ConditionFlag)`.
* Use it to reset the "already notified" state on `Logout`. `CfPop` ("fired when a duty is ready") isn't a PF signal, but it's a useful neighbouring event.

**[`IPlayerState`](https://dalamud.dev/api/Dalamud.Plugin.Services/Interfaces/IPlayerState)** in `Dalamud.Plugin.Services`. Injected with `[PluginService]` (already in the template). `bool IsLoaded`, `ulong ContentId`, `string CharacterName`, `uint EntityId`, `RowRef<World> HomeWorld` / `CurrentWorld`. Use `ContentId` to recognise your own PF listing or party member entry.

**[`IObjectTable`](https://dalamud.dev/api/Dalamud.Plugin.Services/Interfaces/IObjectTable)** (optional): `IPlayerCharacter? LocalPlayer`, "the local player character, if one is present."

**[`ICondition`](https://dalamud.dev/api/Dalamud.Plugin.Services/Interfaces/ICondition)** in `Dalamud.Plugin.Services`. Injected with `[PluginService]`. `bool this[ConditionFlag flag]`, `bool Any(params ConditionFlag[] flags)`, and `event ConditionChangeDelegate ConditionChange` (delegate [`void (ConditionFlag flag, bool value)`](https://dalamud.dev/api/Dalamud.Plugin.Services/Delegates/ICondition.ConditionChangeDelegate), "Should only get fired for actual changes"). Relevant flags are listed in §1 option C. `BoundByDuty = 34` and `BetweenAreas = 45` help suppress false triggers while zoning **(unverified relevance)**.

---

## 3. Settings: configuration, windowing and ImGui

**Persistence.** [`IPluginConfiguration`](https://dalamud.dev/api/Dalamud.Configuration/Interfaces/IPluginConfiguration) (`Dalamud.Configuration`) has one member, `int Version { get; set; }`. The config class is a plain `[Serializable]` class, as in the template, and isn't injected.
[`IDalamudPluginInterface`](https://dalamud.dev/api/Dalamud.Plugin/Interfaces/IDalamudPluginInterface) (`Dalamud.Plugin`), injected with `[PluginService]`:

| Member | Description (from docs) |
|---|---|
| `void SavePluginConfig(IPluginConfiguration? currentConfig)` | "Save a plugin configuration(inheriting IPluginConfiguration)." |
| `IPluginConfiguration? GetPluginConfig()` | "A previously saved config or null if none was saved before." |
| `DirectoryInfo ConfigDirectory`, `FileInfo ConfigFile` | Where config lives. Useful for telling the user where the token is stored. |
| `IUiBuilder UiBuilder` | UI hooks (see below). |
| `IPluginManifest Manifest`, `FileInfo AssemblyLocation`, `bool IsDev`, `PluginLoadReason Reason` | Metadata. |

The config file is plain serialized data in `ConfigDirectory`, so the ntfy token is stored unencrypted there. Whether Dalamud offers any secret storage is **unverified**; none appears in the docs reviewed.

**UI hooks.** [`IUiBuilder`](https://dalamud.dev/api/Dalamud.Interface/Interfaces/IUiBuilder) (`Dalamud.Interface`) is reached through `PluginInterface.UiBuilder` and isn't injected. Events (all `System.Action`): `Draw` ("when Dalamud is ready to draw your windows... you can use static ImGui calls"), `OpenConfigUi` (the settings button in `/xlplugins`), `OpenMainUi`, `ShowUi` / `HideUi`.

**Windowing.** [`Dalamud.Interface.Windowing`](https://dalamud.dev/api/Dalamud.Interface.Windowing/):

* [`WindowSystem`](https://dalamud.dev/api/Dalamud.Interface.Windowing/Classes/WindowSystem) (implements `IWindowSystem`): `AddWindow(IWindow)`, `RemoveWindow(IWindow)`, `RemoveAllWindows()`, `Draw()`, `string? Namespace`. `RemoveWindow` and `RemoveAllWindows` don't dispose windows, so dispose them yourself, as the template does. No constructor is shown in the docs; the template uses `new WindowSystem("SamplePlugin")`.
* [`Window`](https://dalamud.dev/api/Dalamud.Interface.Windowing/Classes/Window) (`public abstract class Window : IWindow`): `abstract void Draw()` ("You do NOT need to ImGui.Begin your window"), `virtual PreDraw()` / `PostDraw()` / `OnOpen()` / `OnClose()`, `void Toggle()`, `bool IsOpen`, `ImGuiWindowFlags Flags`, `Vector2? Size`, `ImGuiCond SizeCondition`, `WindowSizeConstraints? SizeConstraints`, `string WindowName` (use `###id` for a stable ID), `bool RespectCloseHotkey`. No constructor is shown on the docs page; the template calls `base(string name)` and `base(string name, ImGuiWindowFlags flags)`.
* `WindowHost` and `IWindow` also exist. They aren't needed; `Window` isn't marked obsolete.

**ImGui bindings.** API 15 uses **`Dalamud.Bindings.ImGui`**. API signatures in the docs reference it, for example `Dalamud.Bindings.ImGui.ImGuiCond` in [`ImGuiHelpers`](https://dalamud.dev/api/Dalamud.Interface.Utility/Classes/ImGuiHelpers) and `Dalamud.Bindings.ImGui.ImGuiCol` in [`ImRaii`](https://dalamud.dev/api/Dalamud.Interface.Utility.Raii/Classes/ImRaii). The [v13 notes](https://dalamud.dev/versions/v13) say: "`ImGuiNET` → `Dalamud.Bindings.ImGui`", derived from Hexa.NET.ImGui, and "If you use Dalamud.NET.Sdk, the new binding assemblies are now automatically referenced". The `ImGui` class itself isn't in the dalamud.dev API index. Take widget signatures (`InputText`, `Button`, `Checkbox`, `Combo`) from IntelliSense. Old `ImGuiNET` samples won't compile as-is.
Helpers: `ImGuiHelpers` (`Dalamud.Interface.Utility`) and `ImRaii` (`Dalamud.Interface.Utility.Raii`). v15 removed `IEndObject` from ImRaii, so older samples may break.

**"Send test notification" button:** `if (ImGui.Button("Send test notification")) { _ = SendAsync(...); }`. Don't wait on the task inside `Draw` (see Gotchas).

---

## 4. Commands

**[`ICommandManager`](https://dalamud.dev/api/Dalamud.Plugin.Services/Interfaces/ICommandManager)** in `Dalamud.Plugin.Services`. Injected with `[PluginService]`.

| Member | Description (from docs) |
|---|---|
| `bool AddHandler(string command, CommandInfo info)` | "Add a command handler..." Returns `true` on success. |
| `bool RemoveHandler(string command)` | Remove it. Call this in `Dispose`. |
| `ReadOnlyDictionary<string, IReadOnlyCommandInfo> Commands` | All registered commands. |
| `bool ProcessCommand(string content)`, `DispatchCommand(...)` | Not needed. |

**[`CommandInfo`](https://dalamud.dev/api/Dalamud.Game.Command/Classes/CommandInfo)** (`Dalamud.Game.Command`, plain class, not injected): `HandlerDelegate Handler`, `string HelpMessage` (shown in `/xlhelp`), `bool ShowInHelp`, `int DisplayOrder`, `bool AllowedInMacros` (defaults to true). No constructor is shown in the docs; the template uses `new CommandInfo(OnCommand) { HelpMessage = "..." }`.
Handler: [`IReadOnlyCommandInfo.HandlerDelegate`](https://dalamud.dev/api/Dalamud.Game.Command/Delegates/IReadOnlyCommandInfo.HandlerDelegate), `void (string command, string arguments)`.

---

## 5. In-game feedback (POST success or failure)

**[`IPluginLog`](https://dalamud.dev/api/Dalamud.Plugin.Services/Interfaces/IPluginLog)** in `Dalamud.Plugin.Services`. Injected with `[PluginService]`. Viewed with `/xllog`.

* `Fatal`, `Error`, `Warning`, `Information` / `Info`, `Debug`, `Verbose`: each takes `(string messageTemplate, params object[] values)`, with an overload that takes `Exception? exception` first.
* `Write(LogEventLevel level, Exception? exception, string messageTemplate, params object[] values)`.
* `ILogger Logger` (Serilog), `LogEventLevel MinimumLogLevel`. The minimum defaults to Debug for downloaded plugins and Verbose for dev plugins.
* Never log the bearer token.

**[`IChatGui`](https://dalamud.dev/api/Dalamud.Plugin.Services/Interfaces/IChatGui)** (see §1) for chat output:

* `void Print(string message, string? messageTag = null, ushort? tagColor = null)`
* `void PrintError(string message, string? messageTag = null, ushort? tagColor = null)`
* Also `SeString` and `ReadOnlySpan<byte>` overloads, and `void Print(XivChatEntry chat)` ([`XivChatEntry`](https://dalamud.dev/api/Dalamud.Game.Text/Classes/XivChatEntry): `XivChatType? Type`, `SeString Message`, `SeString Name`, `bool Silent`, and more).
* The docs say of every overload: "Queue a chat message. Dalamud will send queued messages on the next framework event."

**[`INotificationManager`](https://dalamud.dev/api/Dalamud.Plugin.Services/Interfaces/INotificationManager)** in `Dalamud.Plugin.Services`. Injected with `[PluginService]`. "Manager for notifications provided by Dalamud using ImGui."

* `IActiveNotification AddNotification(Notification notification)`.
* [`Notification`](https://dalamud.dev/api/Dalamud.Interface.ImGuiNotification/Classes/Notification) (plain class): `string Content`, `string? Title`, `NotificationType Type`, `TimeSpan InitialDuration`, `string? MinimizedText`, `bool UserDismissable`, and more.
* [`NotificationType`](https://dalamud.dev/api/Dalamud.Interface.ImGuiNotification/Enums/NotificationType): `None = 0`, `Success = 1`, `Warning = 2`, `Error = 3`, `Info = 4`.
* [`IActiveNotification`](https://dalamud.dev/api/Dalamud.Interface.ImGuiNotification/Interfaces/IActiveNotification) is returned for later updates; its members weren't reviewed.

**[`IToastGui`](https://dalamud.dev/api/Dalamud.Plugin.Services/Interfaces/IToastGui)** in `Dalamud.Plugin.Services`. Injected with `[PluginService]`. Native game toasts:

* `void ShowNormal(string message, ToastOptions? options = null)`
* `void ShowQuest(string message, QuestToastOptions? options = null)`
* `void ShowError(string message)`
* `SeString` overloads of each.
* Options types are in [`Dalamud.Game.Gui.Toast`](https://dalamud.dev/api/Dalamud.Game.Gui.Toast/) (`ToastOptions`, `ToastPosition`, `ToastSpeed`).
* Suggestion: `INotificationManager` for the result of the test button, `PrintError` and the log for failures of real alerts **(design choice)**.

---

## 6. HTTP

Dalamud provides **no HTTP client service**. The [v9 notes](https://dalamud.dev/versions/v9/) say: "`Util.HttpClient` has been removed in favor of allowing plugins to manage their own HTTP lifecycles. You can use `Dalamud.Networking.Http.HappyEyeballsCallback` as your `SocketsHttpHandler.ConnectCallback` to enable improved IPv6 connection handling to dual-stack servers."

* Use plain **`System.Net.Http.HttpClient`**. Create one per plugin with a `Timeout` set, add the `Authorization: Bearer`, `Title` and `Priority` headers per request, and dispose it in `Plugin.Dispose()`.
* Optional: [`HappyEyeballsCallback`](https://dalamud.dev/api/Dalamud.Networking.Http/Classes/HappyEyeballsCallback) (`Dalamud.Networking.Http`, implements `IDisposable`). It has `ValueTask<Stream> ConnectCallback(SocketsHttpConnectionContext context, CancellationToken token)`, which you assign to `SocketsHttpHandler.ConnectCallback`. No constructor is shown in the docs; a parameterless one is **(unverified)**. Dispose it with the handler. It only matters if the ntfy host is dual-stack and IPv6 is flaky. Plain `HttpClient` works without it.

---

## Gotchas and open questions

**Cross-world parties and `IPartyList` (answered by PartyVet).**
* The dalamud.dev pages say nothing about cross-world parties. PartyVet's working code does: Party Finder and cross-world parties aren't in `IPartyList` until the party enters a duty. Read `InfoProxyCrossRealm` instead; see §1 option A for the snippet.
* The SDK references Dalamud's bundled FFXIVClientStructs by default. This is implied by the documented opt-out property `<Use_Dalamud_FFXIVClientStructs>false</Use_Dalamud_FFXIVClientStructs>` on [Using Custom ClientStructs](https://dalamud.dev/plugin-development/reverse-engineering/using-custom-cs/), and confirmed by PartyVet compiling against it without a package reference.
* `ConditionFlag.ParticipatingInCrossWorldPartyOrAlliance` (84) also says when you're in a cross-world party.

**Threading.**
* `IFramework.Update` runs on the framework (game) thread. This is implied by `IsInFrameworkUpdateThread` and the `Run` docs. Keep handlers cheap.
* `UiBuilder.Draw` runs on the render thread, not the framework thread (per PartyVet's `.claude/context/dalamud-ui.md`). Never read `IPartyList` or `InfoProxyCrossRealm` from a window's `Draw`; read state the framework thread has published instead.
* Never do the HTTP POST there, and never `.Wait()` or `.Result` on the framework thread. The `IFramework` docs warn that "Starting new tasks and waiting on them synchronously from this callback will completely lock up the game."
* Start the POST with `Task.Run` (or an un-awaited async method). The docs explicitly say to use `Task.Run` because `Task.Factory.StartNew` / `ContinueWith` use `TaskScheduler.Current`, which inside `Run` callbacks is the framework thread.
* Which thread `IChatGui.ChatMessage`, `LogMessage` and `IPartyFinderGui.ReceiveListing` fire on isn't stated. They're presumably the game thread, since they're hooks on game functions **(unverified)**. Treat them like `Update`: record state, don't block.
* After the POST completes on a thread-pool thread:
  * `IChatGui.Print` is documented as queued until the next framework event, so it's likely safe from any thread **(unverified)**.
  * Thread-safety of `INotificationManager.AddNotification` and `IToastGui.Show*` isn't documented. Marshal them with `IFramework.RunOnFrameworkThread(Action)` to be safe.
  * `IPluginLog` is Serilog-backed and is presumably thread-safe **(unverified)**.
* `ILogMessage.Parameters` "must not be accessed after returning from" the event. Copy what you need before going async.
* v15 added `IAsyncDalamudPlugin`, where init and dispose run off the main thread. The plugin doesn't need it; stay with `IDalamudPlugin` as in the template.

**Deprecated or changed (relevant if copying older examples).**
* `IClientState.LocalPlayer` and `LocalContentId` were made obsolete in v14 and no longer appear on the API 15 `IClientState` page. Use `IObjectTable.LocalPlayer` and `IPlayerState.ContentId`.
* v15 changed `IChatGui` event signatures: parameters moved into `IChatMessage` / `IHandleableChatMessage`. Pre-v15 chat handler samples won't compile.
* `IFramework.RunOnFrameworkThread(Func<Task>)` and `RunOnFrameworkThread<T>(Func<Task<T>>)` are `[Obsolete("Use RunOnTick instead.")]`. The `Action` and `Func<T>` overloads aren't obsolete.
* `IPartyMember.ObjectId` is obsolete; use `EntityId`.
* `ImGuiNET` was replaced by `Dalamud.Bindings.ImGui` in v13. ImRaii's `IEndObject` was removed in v15.

**Other open questions.**
* **(unverified)** Which `XivChatType` / `LogMessageId` the "recruitment complete" message uses. Discover it in-game.
* **(unverified)** Whether `IPartyFinderGui.ReceiveListing` ever delivers the player's own listing, and whether it does so with the PF window closed.
* **(unverified)** What `ConditionFlag.UsingPartyFinder` means exactly: window open, or listing active.
* `IPartyList` includes the local player when it's populated, per PartyVet. Whether `InfoProxyCrossRealm`'s group does is **(unverified)**.
* **Docs site quirk:** the version dropdown also lists `/api/api16/`, labelled "15.x (API 15) [Legacy]" and "no longer actively maintained". This document uses `/api/` ("15.x (API 15) [Current]"), whose `IChatGui` page matches the v15 release notes.
