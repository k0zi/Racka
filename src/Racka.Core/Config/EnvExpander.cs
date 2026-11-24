using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace Racka.Core.Config;

/// <summary>Expands ${VAR} and ${VAR:-default} references.</summary>
public static partial class EnvExpander
{
    [GeneratedRegex(@"\$\{([A-Za-z_][A-Za-z0-9_]*)(?::-([^}]*))?\}")]
    private static partial Regex VariablePattern();

    public static string Expand(string value, Func<string, string?>? lookup = null)
    {
        lookup ??= Environment.GetEnvironmentVariable;
        return VariablePattern().Replace(value, m =>
        {
            var name = m.Groups[1].Value;
            var resolved = lookup(name);
            if (!string.IsNullOrEmpty(resolved))
                return resolved;
            if (m.Groups[2].Success)
                return m.Groups[2].Value;
            throw new ConfigException($"Environment variable '{name}' is not set.");
        });
    }
}

/// <summary>Applies env expansion to every scalar that is deserialized into a string.</summary>
internal sealed class EnvExpandingStringDeserializer(Func<string, string?>? lookup) : INodeDeserializer
{
    public bool Deserialize(IParser reader, Type expectedType, Func<IParser, Type, object?> nestedObjectDeserializer,
        out object? value, ObjectDeserializer rootDeserializer)
    {
        if (expectedType == typeof(string) && reader.Accept<Scalar>(out var scalar) && !IsNull(scalar))
        {
            reader.MoveNext();
            value = EnvExpander.Expand(scalar.Value, lookup);
            return true;
        }
        value = null;
        return false;
    }

    private static bool IsNull(Scalar scalar) =>
        scalar.Style == ScalarStyle.Plain && scalar.Value is "" or "~" or "null" or "Null" or "NULL";
}
