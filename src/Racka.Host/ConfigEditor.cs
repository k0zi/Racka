using System.Collections;
using System.Reflection;
using Racka.Core.Agents;
using Racka.Core.Config;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;

namespace Racka.Host;

/// <summary>
/// `racka config get/set/unset`: edits config.yaml (or an agent.yaml) by dotted key, checked against the config
/// classes so a typo is an error, not a silently ignored key. Rewriting the file drops its comments.
/// </summary>
internal sealed class ConfigEditor
{
    private readonly Type _schema;
    private readonly YamlMappingNode _root;

    private ConfigEditor(string path, Type schema, YamlMappingNode root)
    {
        Path = path;
        _schema = schema;
        _root = root;
    }

    public string Path { get; }

    public static ConfigEditor OpenUserSettings(string? path = null) =>
        Open(path ?? RackaDirectories.ConfigFile, typeof(UserSettingsFile));

    public static ConfigEditor OpenAgent(string agentDirectory)
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetFullPath(agentDirectory), AgentDefinition.ConfigFileName);
        if (!File.Exists(path))
            throw new ConfigException($"Missing {AgentDefinition.ConfigFileName} in '{agentDirectory}'.");
        return Open(path, typeof(AgentConfig));
    }

    private static ConfigEditor Open(string path, Type schema) =>
        new(path, schema, File.Exists(path) ? RackaYaml.LoadMapping(File.ReadAllText(path), path) : new YamlMappingNode());

    /// <summary>The value at the key: a scalar as is, a list or section as YAML. Null when it is not set.</summary>
    public string? Get(string key)
    {
        Resolve(key);
        YamlNode node = _root;
        foreach (var segment in key.Split('.'))
        {
            if (node is not YamlMappingNode mapping || !mapping.Children.TryGetValue(new YamlScalarNode(segment), out var child))
                return null;
            node = child;
        }
        return node switch
        {
            YamlScalarNode scalar => scalar.Value,
            YamlSequenceNode sequence => string.Join(',', sequence.Children.OfType<YamlScalarNode>().Select(s => s.Value)),
            YamlMappingNode mapping => RackaYaml.Save(mapping).TrimEnd(),
            _ => null
        };
    }

    /// <summary>Sets a value; lists take comma separated items ("#a,#b"), an empty value gives an empty list.</summary>
    public void Set(string key, string value)
    {
        var type = Resolve(key);
        YamlNode node;
        if (IsList(type))
        {
            node = new YamlSequenceNode(value.Split(',').Select(v => v.Trim()).Where(v => v.Length > 0).Select(v => new YamlScalarNode(v)));
        }
        else if (IsScalar(type))
        {
            var scalarType = Nullable.GetUnderlyingType(type) ?? type;
            if (scalarType == typeof(bool) && !bool.TryParse(value, out _))
                throw new ConfigException($"{key} takes true or false.");
            if ((scalarType == typeof(int) && !int.TryParse(value, out _)) || (scalarType == typeof(decimal) && !decimal.TryParse(value, out _)))
                throw new ConfigException($"{key} takes a number.");
            node = new YamlScalarNode(value);
        }
        else
        {
            throw new ConfigException($"{key} is a section; set one of its keys, e.g. {key}.{Keys(type).FirstOrDefault()}.");
        }

        var segments = key.Split('.');
        var mapping = _root;
        foreach (var segment in segments[..^1])
        {
            var segmentKey = new YamlScalarNode(segment);
            if (!mapping.Children.TryGetValue(segmentKey, out var child) || child is not YamlMappingNode childMapping)
            {
                childMapping = new YamlMappingNode();
                mapping.Children[segmentKey] = childMapping;
            }
            mapping = childMapping;
        }
        mapping.Children[new YamlScalarNode(segments[^1])] = node;
    }

    /// <summary>Removes a key, and the sections it leaves empty. Returns false when it was not set.</summary>
    public bool Unset(string key)
    {
        Resolve(key);
        return Remove(_root, key.Split('.'));

        static bool Remove(YamlMappingNode mapping, string[] segments)
        {
            var segmentKey = new YamlScalarNode(segments[0]);
            if (!mapping.Children.TryGetValue(segmentKey, out var child))
                return false;
            if (segments.Length == 1)
                return mapping.Children.Remove(segmentKey);
            if (child is not YamlMappingNode childMapping || !Remove(childMapping, segments[1..]))
                return false;
            if (childMapping.Children.Count == 0)
                mapping.Children.Remove(segmentKey);
            return true;
        }
    }

    public string Text => RackaYaml.Save(_root);

    /// <summary>Validates the result against the schema and writes it (0600, in a 0700 directory for the user settings).</summary>
    public void Save()
    {
        var text = Text;
        try
        {
            RackaYaml.LiteralDeserializer.Deserialize(text, _schema);
        }
        catch (YamlDotNet.Core.YamlException e)
        {
            throw new ConfigException($"The change would make {Path} invalid: {e.InnerException?.Message ?? e.Message}");
        }
        var dir = System.IO.Path.GetDirectoryName(Path)!;
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var isNew = !File.Exists(Path);
        File.WriteAllText(Path, text);
        if (isNew && !OperatingSystem.IsWindows())
            File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    /// <summary>The C# type a dotted key points to; throws with the valid keys when a segment is unknown.</summary>
    private Type Resolve(string key)
    {
        var type = _schema;
        var path = "";
        foreach (var segment in key.Split('.'))
        {
            if (segment.Length == 0)
                throw new ConfigException($"Invalid key '{key}'.");
            if (GetDictionaryValueType(type) is { } valueType)
            {
                type = valueType; // free-form names, e.g. mcpServers.<name>
            }
            else
            {
                var property = Properties(type).FirstOrDefault(p => p.Name == segment);
                if (property.Type == null)
                    throw new ConfigException($"Unknown key '{(path.Length == 0 ? "" : path + ".")}{segment}'. " +
                                              $"Valid keys{(path.Length == 0 ? "" : $" under {path}")}: {string.Join(", ", Keys(type))}.");
                type = property.Type;
            }
            path = path.Length == 0 ? segment : $"{path}.{segment}";
        }
        return type;
    }

    private static IEnumerable<string> Keys(Type type) => Properties(type).Select(p => p.Name);

    private static IEnumerable<(string Name, Type Type)> Properties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && p.GetCustomAttribute<YamlIgnoreAttribute>() == null)
            .Select(p => (p.GetCustomAttribute<YamlMemberAttribute>()?.Alias ?? char.ToLowerInvariant(p.Name[0]) + p.Name[1..], p.PropertyType));

    private static bool IsScalar(Type type) =>
        (Nullable.GetUnderlyingType(type) ?? type) is var t && (t.IsPrimitive || t == typeof(string) || t == typeof(decimal));

    private static bool IsList(Type type) =>
        type != typeof(string) && GetDictionaryValueType(type) == null && typeof(IEnumerable).IsAssignableFrom(type);

    private static Type? GetDictionaryValueType(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>) ? type.GetGenericArguments()[1] : null;
}
