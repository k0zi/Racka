namespace Racka.Core.Messaging;

/// <summary>Where agent sockets live: $RACKA_RUNTIME_DIR, else $XDG_RUNTIME_DIR/racka, else /tmp/racka-$USER.</summary>
public sealed class BusPaths
{
    public const string SocketExtension = ".sock";

    public BusPaths(string? runtimeDirectory = null)
    {
        RuntimeDirectory = runtimeDirectory ?? DefaultRuntimeDirectory();
    }

    public string RuntimeDirectory { get; }

    /// <summary>Precedence: --runtime-dir, then $RACKA_RUNTIME_DIR, then runtimeDir in the user settings, then the default.</summary>
    public static BusPaths Resolve(string? commandLine, string? userSetting)
    {
        if (!string.IsNullOrEmpty(commandLine))
            return new BusPaths(commandLine);
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RACKA_RUNTIME_DIR")) && !string.IsNullOrEmpty(userSetting))
            return new BusPaths(userSetting);
        return new BusPaths();
    }

    /// <summary>Unix socket paths are limited to 108 bytes including the terminating NUL on Linux.</summary>
    private const int MaxSocketPathBytes = 107;

    public string SocketPath(string agentName)
    {
        var path = Path.Combine(RuntimeDirectory, agentName + SocketExtension);
        if (System.Text.Encoding.UTF8.GetByteCount(path) > MaxSocketPathBytes)
            throw new Config.ConfigException(
                $"Socket path '{path}' is longer than {MaxSocketPathBytes} bytes. Set RACKA_RUNTIME_DIR or --runtime-dir to a shorter directory.");
        return path;
    }

    public void EnsureRuntimeDirectory()
    {
        Directory.CreateDirectory(RuntimeDirectory);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(RuntimeDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public IReadOnlyList<string> ListAgentNames() =>
        Directory.Exists(RuntimeDirectory)
            ? Directory.EnumerateFiles(RuntimeDirectory, "*" + SocketExtension)
                .Select(Path.GetFileNameWithoutExtension)
                .OfType<string>()
                .Order(StringComparer.Ordinal)
                .ToList()
            : [];

    private static string DefaultRuntimeDirectory()
    {
        var explicitDir = Environment.GetEnvironmentVariable("RACKA_RUNTIME_DIR");
        if (!string.IsNullOrEmpty(explicitDir))
            return explicitDir;
        var xdg = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (!string.IsNullOrEmpty(xdg))
            return Path.Combine(xdg, "racka");
        return Path.Combine(Path.GetTempPath(), "racka-" + Environment.UserName);
    }
}
