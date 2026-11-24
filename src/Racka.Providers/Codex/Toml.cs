using System.Globalization;
using System.Text;

namespace Racka.Providers.Codex;

/// <summary>Just enough TOML to write `codex -c key=value` overrides.</summary>
internal static class Toml
{
    public static string String(string value)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    // Other control characters need \u escapes; everything else, emoji included, stays literal.
                    if (c < 0x20 || c == 0x7f)
                        sb.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                    else
                        sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    public static string Array(IEnumerable<string> values) => "[" + string.Join(", ", values.Select(String)) + "]";

    public static string Table(IEnumerable<KeyValuePair<string, string>> values) =>
        "{" + string.Join(", ", values.Select(kv => $"{Key(kv.Key)} = {String(kv.Value)}")) + "}";

    /// <summary>A bare key when possible, a quoted one otherwise.</summary>
    public static string Key(string key) =>
        key.Length > 0 && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') ? key : String(key);
}
