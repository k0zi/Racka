namespace Racka.Chat;

public enum ChatCommandKind
{
    Task,
    Status,
    Stop,
    Reset,
    Help
}

public sealed record ChatCommand(ChatCommandKind Kind, string Text = "");

public static class ChatCommandParser
{
    private static readonly Dictionary<string, ChatCommandKind> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["status"] = ChatCommandKind.Status,
        ["stop"] = ChatCommandKind.Stop,
        ["reset"] = ChatCommandKind.Reset,
        ["help"] = ChatCommandKind.Help
    };

    /// <summary>
    /// Channel: "nick: task", "nick, task", "!nick task", "!status [nick]", "!stop [nick]", "!reset [nick]", "!help",
    /// or any text when the platform reports that the bot was mentioned (the mention already stripped).
    /// Private message: the whole text is the task, unless it is a "!command".
    /// Returns null when the message is not meant for this agent.
    /// </summary>
    public static ChatCommand? Parse(string text, string nick, string prefix, bool isPrivate, bool mentionsMe = false)
    {
        text = text.Trim();
        if (text.Length == 0)
            return null;

        if (text.StartsWith(prefix, StringComparison.Ordinal))
        {
            var body = text[prefix.Length..].Trim();
            var (word, rest) = SplitFirst(body);
            if (Keywords.TryGetValue(word, out var kind))
                return rest.Length == 0 || IsNick(rest, nick) ? new ChatCommand(kind) : null;
            if (IsNick(word, nick))
                return Addressed(rest);
            return isPrivate || mentionsMe ? new ChatCommand(ChatCommandKind.Task, body) : null;
        }

        if (isPrivate)
            return new ChatCommand(ChatCommandKind.Task, text);

        foreach (var separator in new[] { ':', ',' })
        {
            var idx = text.IndexOf(separator);
            if (idx > 0 && IsNick(text[..idx], nick))
                return Addressed(text[(idx + 1)..].Trim());
        }
        return mentionsMe ? Addressed(text) : null;
    }

    private static ChatCommand? Addressed(string rest)
    {
        if (rest.Length == 0)
            return null;
        return Keywords.TryGetValue(rest, out var kind) ? new ChatCommand(kind) : new ChatCommand(ChatCommandKind.Task, rest);
    }

    private static bool IsNick(string candidate, string nick) =>
        string.Equals(candidate.Trim(), nick, StringComparison.OrdinalIgnoreCase);

    private static (string Head, string Tail) SplitFirst(string s)
    {
        var space = s.IndexOf(' ');
        return space < 0 ? (s, "") : (s[..space], s[(space + 1)..].Trim());
    }
}
