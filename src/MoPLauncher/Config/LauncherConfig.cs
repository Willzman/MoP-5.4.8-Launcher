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

    // Lets the login, character list and character creation screens use the
    // full width of screens wider than 16:9, instead of the black bars the
    // client leaves at the sides. Off by default while it is being tried.
    public bool WideMenus { get; set; }

    // Where the "What's new" panel reads the realm news from. Empty means the
    // server repository's REALM_NEWS.md on Development. Not in Settings; set
    // it in config.json to follow another branch or realm.
    public string NewsUrl { get; set; } = string.Empty;

    // Plays the client's own login screen music while the launcher is open.
    public bool MusicEnabled { get; set; } = true;

    // 0 to 1.
    public double MusicVolume { get; set; } = 0.1;

    // Sends the realm's developers the screenshot of each bug report sent
    // with the Bug Report add-on, new crash logs and the Error Catcher's Lua
    // errors (see Feedback\FeedbackService). Other screenshots never leave
    // the PC.
    public bool FeedbackUploads { get; set; } = true;

    // Also sends crash dumps (.dmp): bigger, and they hold part of the game's
    // memory. Off unless the player turns it on.
    public bool FeedbackUploadDumps { get; set; }

    // Where the feedback upload API is and its certificate's SHA-256. Empty
    // means the login server's host on port 8443 and ServerDefaults'
    // certificate. Not in Settings; set them in config.json.
    public string FeedbackUrl { get; set; } = string.Empty;
    public string FeedbackCertificate { get; set; } = string.Empty;

    // The account to play as: filled in on the game's login screen when set.
    public string AccountName { get; set; } = string.Empty;

    // Every account entered in Settings or seen in the game's own settings;
    // the launcher offers them in a drop-down once there is more than one.
    public List<SavedAccount> Accounts { get; set; } = [];

    // Auto-login: after Play, the launcher types the chosen account's
    // password into the game's login screen. Passwords are kept encrypted
    // (see SavedPassword) and only while auto-login is on.
    public bool AutoLogin { get; set; }

    // The realm to enter, written to the game's realmName setting before it
    // starts. Empty leaves the game on the realm it remembers.
    public string Realm { get; set; } = string.Empty;

    // Realms the game has been seen on, for the realm drop-down.
    public List<string> Realms { get; set; } = [];

    // Where 1.3.0 kept its single password; moved into Accounts on load.
    public string ProtectedPassword { get; set; } = string.Empty;

    public SavedAccount? FindAccount(string name) =>
        Accounts.Find(a => string.Equals(a.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    // Adds the account if it's new. Returns the saved entry, or null for a blank name.
    public SavedAccount? RememberAccount(string name)
    {
        name = name.Trim();
        if (name.Length == 0)
            return null;

        var account = FindAccount(name);
        if (account is null)
        {
            account = new SavedAccount { Name = name };
            Accounts.Add(account);
        }

        return account;
    }

    // Returns true when the realm was new.
    public bool RememberRealm(string name)
    {
        name = name.Trim();
        if (name.Length == 0 || Realms.Exists(r => string.Equals(r, name, StringComparison.OrdinalIgnoreCase)))
            return false;

        Realms.Add(name);
        return true;
    }

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

public class SavedAccount
{
    public string Name { get; set; } = string.Empty;

    // Empty when no password is saved for this account.
    public string ProtectedPassword { get; set; } = string.Empty;
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

        // A hand-edited file can leave these null or with blank entries.
        config.Accounts = (config.Accounts ?? []).Where(a => !string.IsNullOrWhiteSpace(a?.Name)).ToList();
        config.Realms = (config.Realms ?? []).Where(r => !string.IsNullOrWhiteSpace(r)).ToList();
        config.AccountName ??= string.Empty;
        config.Realm ??= string.Empty;
        config.FeedbackUrl ??= string.Empty;
        config.FeedbackCertificate ??= string.Empty;

        // 1.3.0 had one account, with its password at the top level.
        if (config.RememberAccount(config.AccountName) is { } current && !string.IsNullOrEmpty(config.ProtectedPassword))
        {
            if (current.ProtectedPassword.Length == 0)
                current.ProtectedPassword = config.ProtectedPassword;
        }
        config.ProtectedPassword = string.Empty;

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
