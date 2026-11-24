using System.Text;
using System.Text.RegularExpressions;

namespace Racka.Chat.Irc;

/// <summary>A parsed IRC protocol line: [@tags] [:prefix] COMMAND [params] [:trailing].</summary>
public sealed record IrcMessage(string? Prefix, string Command, IReadOnlyList<string> Parameters)
{
    public string? Nick => Prefix is null ? null : Prefix.Split('!', 2)[0];

    public string? Trailing => Parameters.Count > 0 ? Parameters[^1] : null;

    public static IrcMessage? Parse(string line)
    {
        var rest = line.TrimEnd('\r', '\n');
        if (rest.Length == 0)
            return null;
        if (rest[0] == '@')
        {
            var space = rest.IndexOf(' ');
            if (space < 0) return null;
            rest = rest[(space + 1)..].TrimStart(' ');
        }
        string? prefix = null;
        if (rest.StartsWith(':'))
        {
            var space = rest.IndexOf(' ');
            if (space < 0) return null;
            prefix = rest[1..space];
            rest = rest[(space + 1)..].TrimStart(' ');
        }

        var parameters = new List<string>();
        string command;
        var firstSpace = rest.IndexOf(' ');
        if (firstSpace < 0)
        {
            command = rest;
            rest = "";
        }
        else
        {
            command = rest[..firstSpace];
            rest = rest[(firstSpace + 1)..];
        }
        while (rest.Length > 0)
        {
            rest = rest.TrimStart(' ');
            if (rest.Length == 0)
                break;
            if (rest[0] == ':')
            {
                parameters.Add(rest[1..]);
                break;
            }
            var space = rest.IndexOf(' ');
            if (space < 0)
            {
                parameters.Add(rest);
                break;
            }
            parameters.Add(rest[..space]);
            rest = rest[(space + 1)..];
        }
        return new IrcMessage(prefix, command.ToUpperInvariant(), parameters);
    }
}

public static partial class IrcText
{
    /// <summary>Max bytes of message text per PRIVMSG so the whole line stays under 512 bytes.</summary>
    public const int MaxPayloadBytes = 400;

    [GeneratedRegex(@"[\x00-\x08\x0B-\x1F\x7F]")]
    private static partial Regex ControlChars();

    /// <summary>Splits text into IRC-safe lines of at most <paramref name="maxBytes"/> UTF-8 bytes, breaking at spaces when possible.</summary>
    public static IReadOnlyList<string> SplitLines(string text, int maxBytes = MaxPayloadBytes)
    {
        var result = new List<string>();
        foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = ControlChars().Replace(rawLine.Replace('\t', ' '), "").TrimEnd();
            if (line.Length == 0)
                continue;
            while (Encoding.UTF8.GetByteCount(line) > maxBytes)
            {
                var cut = FitLength(line, maxBytes);
                var space = line.LastIndexOf(' ', cut - 1, cut);
                if (space > cut / 2)
                    cut = space;
                result.Add(line[..cut].TrimEnd());
                line = line[cut..].TrimStart();
            }
            if (line.Length > 0)
                result.Add(line);
        }
        return result;
    }

    /// <summary>Largest prefix length (in chars, not splitting surrogate pairs) that fits in maxBytes.</summary>
    private static int FitLength(string s, int maxBytes)
    {
        var bytes = 0;
        var i = 0;
        while (i < s.Length)
        {
            var len = char.IsHighSurrogate(s[i]) && i + 1 < s.Length ? 2 : 1;
            var size = Encoding.UTF8.GetByteCount(s.AsSpan(i, len));
            if (bytes + size > maxBytes)
                break;
            bytes += size;
            i += len;
        }
        return Math.Max(i, 1);
    }

    /// <summary>Matches nick!user@host against a glob mask with * and ?, case-insensitively.</summary>
    public static bool MatchesMask(string prefix, string mask)
    {
        var pattern = "^" + Regex.Escape(mask).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return Regex.IsMatch(prefix, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
