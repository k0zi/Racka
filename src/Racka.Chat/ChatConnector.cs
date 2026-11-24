using System.Text;
using Racka.Core.Config;

namespace Racka.Chat;

/// <summary>Where a message goes: a channel (or chat, conversation, IRC nick) and optionally a thread in it.</summary>
public sealed record ChatAddress(string ChannelId, string? ThreadId = null);

/// <param name="ReplyTo">Where the answer goes: the channel or thread, or the private chat.</param>
/// <param name="SenderId">Identity checked against the operators: IRC nick!user@host, elsewhere the platform user id.</param>
/// <param name="SenderName">Human readable sender; the task's "from".</param>
/// <param name="Text">The message text, with the mention of the bot removed.</param>
/// <param name="ConversationId">Messages with the same id share history: the channel, or the sender in private.</param>
/// <param name="MentionsMe">The platform says the bot was mentioned (or replied to), so the whole text is for it.</param>
public sealed record ChatMessage(
    ChatAddress ReplyTo,
    string SenderId,
    string SenderName,
    string Text,
    bool IsPrivate,
    string ConversationId,
    bool MentionsMe = false);

/// <summary>One chat platform connection of an agent. The platform independent behaviour lives in <see cref="ChatService"/>.</summary>
public interface IChatConnector : IAsyncDisposable
{
    ChatConfig Config { get; }

    string Platform => Config.Platform;

    bool IsConnected { get; }

    /// <summary>The name people address the agent with ("name: task", "!name task").</summary>
    string Name { get; }

    event Action<ChatMessage>? MessageReceived;

    /// <summary>Connects and keeps the connection up (reconnecting) until cancelled.</summary>
    Task RunAsync(CancellationToken ct);

    Task SendAsync(ChatAddress to, string text, CancellationToken ct);

    /// <summary>A short note to the sender alone where the platform allows it (IRC NOTICE), otherwise a reply.</summary>
    Task SendNoticeAsync(ChatMessage to, string text, CancellationToken ct);

    bool IsOperator(ChatMessage message);

    /// <summary>How a channel reply addresses the sender ("k0zi: ", "&lt;@123&gt; ").</summary>
    string AddressOf(ChatMessage message);

    /// <summary>Splits a reply into the lines that count against maxReplyLines.</summary>
    IReadOnlyList<string> SplitLines(string text);
}

/// <summary>Defaults for connectors whose operators are plain user ids and whose messages can hold many lines.</summary>
public abstract class ChatConnectorBase : IChatConnector
{
    public abstract ChatConfig Config { get; }
    public abstract bool IsConnected { get; }
    public abstract string Name { get; }
    public event Action<ChatMessage>? MessageReceived;

    public abstract Task RunAsync(CancellationToken ct);
    public abstract Task SendAsync(ChatAddress to, string text, CancellationToken ct);

    public virtual Task SendNoticeAsync(ChatMessage to, string text, CancellationToken ct) =>
        SendAsync(to.ReplyTo, (to.IsPrivate ? "" : AddressOf(to)) + text, ct);

    public virtual bool IsOperator(ChatMessage message) =>
        Config.Operators.Any(o => string.Equals(o.Trim(), message.SenderId, StringComparison.OrdinalIgnoreCase));

    public virtual string AddressOf(ChatMessage message) => $"{message.SenderName}: ";

    public virtual IReadOnlyList<string> SplitLines(string text) => ChatText.Lines(text);

    /// <summary>Channels to listen in; an empty list means all of them.</summary>
    protected bool IsListenedChannel(string channelId, string? channelName = null) =>
        Config.Channels.Count == 0
        || Config.Channels.Any(c => string.Equals(c, channelId, StringComparison.OrdinalIgnoreCase)
                                    || (channelName != null && string.Equals(c.TrimStart('#'), channelName.TrimStart('#'), StringComparison.OrdinalIgnoreCase)));

    protected void Raise(ChatMessage message) => MessageReceived?.Invoke(message);

    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public static class ChatText
{
    /// <summary>Lines of a reply without trailing blank lines.</summary>
    public static IReadOnlyList<string> Lines(string text)
    {
        var lines = text.ReplaceLineEndings("\n").Split('\n').Select(l => l.TrimEnd()).ToList();
        while (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    /// <summary>Splits text into messages of at most <paramref name="maxChars"/>, preferring line breaks, never splitting a surrogate pair.</summary>
    public static IReadOnlyList<string> Chunk(string text, int maxChars)
    {
        var chunks = new List<string>();
        var current = new StringBuilder();
        foreach (var rawLine in text.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = rawLine;
            while (line.Length > maxChars)
            {
                Flush();
                var cut = maxChars;
                if (char.IsHighSurrogate(line[cut - 1]))
                    cut--;
                var space = line.LastIndexOf(' ', cut - 1, cut);
                if (space > cut / 2)
                    cut = space;
                chunks.Add(line[..cut]);
                line = line[cut..].TrimStart();
            }
            if (current.Length > 0 && current.Length + 1 + line.Length > maxChars)
                Flush();
            if (current.Length > 0)
                current.Append('\n');
            current.Append(line);
        }
        Flush();
        return chunks;

        void Flush()
        {
            var chunk = current.ToString().TrimEnd();
            if (chunk.Trim().Length > 0)
                chunks.Add(chunk);
            current.Clear();
        }
    }
}
