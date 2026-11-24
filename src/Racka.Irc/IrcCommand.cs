namespace Racka.Irc;

public enum IrcCommandKind
{
    Task,
    Status,
    Stop,
    Reset,
    Help
}

public sealed record IrcCommand(IrcCommandKind Kind, string Text = "");

public static class IrcCommandParser
{
    private static readonly Dictionary<string, IrcCommandKind> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["status"] = IrcCommandKind.Status,
        ["stop"] = IrcCommandKind.Stop,
        ["reset"] = IrcCommandKind.Reset,
        ["help"] = IrcCommandKind.Help
    };

    /// <summary>
    /// Channel: "nick: task", "nick, task", "!nick task", "!status [nick]", "!stop [nick]", "!reset [nick]", "!help".
    /// Private message: the whole text is the task, unless it is a "!command".
    /// Returns null when the message is not meant for this agent.
    /// </summary>
    public static IrcCommand? Parse(string text, string nick, string prefix, bool isPrivate)
    {
        text = text.Trim();
        if (text.Length == 0)
            return null;

        if (text.StartsWith(prefix, StringComparison.Ordinal))
        {
            var body = text[prefix.Length..].Trim();
            var (word, rest) = SplitFirst(body);
            if (Keywords.TryGetValue(word, out var kind))
                return rest.Length == 0 || IsNick(rest, nick) ? new IrcCommand(kind) : null;
            if (IsNick(word, nick))
                return Addressed(rest);
            return isPrivate ? new IrcCommand(IrcCommandKind.Task, body) : null;
        }

        if (isPrivate)
            return new IrcCommand(IrcCommandKind.Task, text);

        foreach (var separator in new[] { ':', ',' })
        {
            var idx = text.IndexOf(separator);
            if (idx > 0 && IsNick(text[..idx], nick))
                return Addressed(text[(idx + 1)..].Trim());
        }
        return null;
    }

    private static IrcCommand? Addressed(string rest)
    {
        if (rest.Length == 0)
            return null;
        return Keywords.TryGetValue(rest, out var kind) ? new IrcCommand(kind) : new IrcCommand(IrcCommandKind.Task, rest);
    }

    private static bool IsNick(string candidate, string nick) =>
        string.Equals(candidate.Trim(), nick, StringComparison.OrdinalIgnoreCase);

    private static (string Head, string Tail) SplitFirst(string s)
    {
        var space = s.IndexOf(' ');
        return space < 0 ? (s, "") : (s[..space], s[(space + 1)..].Trim());
    }
}
