using System.Text.Json;

namespace Racka.Providers.ClaudeCode;

/// <summary>Persists Claude Code session ids per conversation key (state/sessions.json).</summary>
public sealed class SessionStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, string> _sessions;

    public SessionStore(string path)
    {
        _path = path;
        _sessions = Load(path);
    }

    public string? Get(string conversationKey)
    {
        lock (_gate) return _sessions.GetValueOrDefault(conversationKey);
    }

    public void Set(string conversationKey, string sessionId)
    {
        lock (_gate)
        {
            _sessions[conversationKey] = sessionId;
            Save();
        }
    }

    public void Remove(string conversationKey)
    {
        lock (_gate)
        {
            if (_sessions.Remove(conversationKey))
                Save();
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_sessions, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, _path, overwrite: true);
    }

    private static Dictionary<string, string> Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new()
                : new();
        }
        catch (JsonException)
        {
            return new();
        }
    }
}
