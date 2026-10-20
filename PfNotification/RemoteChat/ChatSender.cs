using System;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace PfNotification.RemoteChat;

public enum ChatSendOutcome
{
    Sent,

    /// <summary>Wrong thread, or the client's UI module isn't up (title screen, loading).</summary>
    NotReady,

    Empty,

    TooLong,

    /// <summary>The game's text filter changed the line, so it wasn't sent.</summary>
    Sanitised,
}

/// <summary>
/// Hands one line to the game client as if it had been typed in the chat box. This is the ONLY non-passive call in
/// the plugin: the client then sends that chat line to the server (see the passive-only rule in docs/dalamud-api.md).
/// Only <see cref="RemoteChatService"/> may call it, with lines <see cref="RemoteChatPolicy"/> accepted, on the
/// framework thread. Same approach as ECommons' Chat.SendMessage: sanitise first, refuse if that changed anything.
/// </summary>
public static unsafe class ChatSender
{
    public const int MaxBytes = 500;

    // ECommons' AllowedEntities mask for chat text: letters, digits, punctuation, CJK and the character list.
    private const AllowedEntities ChatEntities = (AllowedEntities)0x27F;

    public static ChatSendOutcome SendLine(string line)
    {
        if (!Plugin.Framework.IsInFrameworkUpdateThread)
        {
            return ChatSendOutcome.NotReady;
        }

        var bytes = Encoding.UTF8.GetBytes(line);
        if (bytes.Length == 0)
        {
            return ChatSendOutcome.Empty;
        }

        if (bytes.Length > MaxBytes)
        {
            return ChatSendOutcome.TooLong;
        }

        var uiModule = UIModule.Instance();
        if (uiModule == null)
        {
            return ChatSendOutcome.NotReady;
        }

        // The native string functions are strlen-based, so hand over a null-terminated copy, as ECommons does.
        var terminated = new byte[bytes.Length + 1];
        bytes.CopyTo(terminated, 0);
        var text = Utf8String.FromSequence(terminated);
        if (text == null)
        {
            return ChatSendOutcome.NotReady;
        }

        try
        {
            text->SanitizeString(ChatEntities, null);
            var sanitised = text->AsSpan();
            if ((sanitised.Length > 0) && (sanitised[^1] == 0))
            {
                sanitised = sanitised[..^1];
            }

            if (!sanitised.SequenceEqual(bytes))
            {
                return ChatSendOutcome.Sanitised;
            }

            uiModule->ProcessChatBoxEntry(text, 0, false);
            return ChatSendOutcome.Sent;
        }
        finally
        {
            text->Dtor(true);
        }
    }
}
