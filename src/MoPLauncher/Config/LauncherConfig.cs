using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MoPLauncher.Config;

public enum ClientArchitecture
{
    Auto,
    X86,
    X64
}

public class LauncherConfig
{
    public string ClientLocation { get; set; } = string.Empty;
    public string ServerAddress { get; set; } = ServerDefaults.ServerAddress;
    public ClientArchitecture Architecture { get; set; } = ClientArchitecture.Auto;
    public bool ClearCacheOnLaunch { get; set; }

    // Sends every IPv4 connection the client opens to the server above, not
    // just the login one. Needed when the realm is listed under an address
    // the player can't reach (a LAN address, say); turn it off only when the
    // world server runs on a different machine from the login server.
    public bool RedirectAllConnections { get; set; } = true;

    // Plays the client's own login screen music while the launcher is open.
    public bool MusicEnabled { get; set; } = true;

    // 0 to 1.
    public double MusicVolume { get; set; } = 0.1;

    // Returns the client executable to start, or null when the folder has none.
    public string? FindClientExecutable()
    {
        if (string.IsNullOrWhiteSpace(ClientLocation) || !Directory.Exists(ClientLocation))
            return null;

        var x64 = Path.Combine(ClientLocation, "Wow-64.exe");
        var x86 = Path.Combine(ClientLocation, "Wow.exe");

        return Architecture switch
        {
            ClientArchitecture.X64 => File.Exists(x64) ? x64 : null,
            ClientArchitecture.X86 => File.Exists(x86) ? x86 : null,
            _ => File.Exists(x64) ? x64 : File.Exists(x86) ? x86 : null
        };
    }
}

public class ConfigStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;

    public ConfigStore()
    {
        // Per-user app data, so the launcher exe itself can live anywhere
        // (including folders a standard user can't write to).
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MoPLauncher");
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "config.json");
    }

    public LauncherConfig Load()
    {
        LauncherConfig config;
        try
        {
            config = File.Exists(_path)
                ? JsonSerializer.Deserialize<LauncherConfig>(File.ReadAllText(_path), Options) ?? new LauncherConfig()
                : new LauncherConfig();
        }
        catch (Exception)
        {
            config = new LauncherConfig();
        }

        // Dropped into the game folder? Then there's nothing to ask.
        if (string.IsNullOrWhiteSpace(config.ClientLocation))
        {
            var own = AppContext.BaseDirectory;
            if (File.Exists(Path.Combine(own, "Wow-64.exe")) || File.Exists(Path.Combine(own, "Wow.exe")))
                config.ClientLocation = own.TrimEnd(Path.DirectorySeparatorChar);
        }

        return config;
    }

    public void Save(LauncherConfig config) =>
        File.WriteAllText(_path, JsonSerializer.Serialize(config, Options));
}
