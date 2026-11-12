namespace MoPLauncher.Config;

// What a fresh install starts with. Change these before publishing a release
// if you want the launcher you hand out to point at your server already.
public static class ServerDefaults
{
    // IP or hostname of the login server, optionally with ":port".
    public const string ServerAddress = "127.0.0.1";

    // Shown under the title, e.g. "My Realm". Leave empty to show the address.
    public const string RealmName = "";
}
