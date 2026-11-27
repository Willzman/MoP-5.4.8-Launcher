namespace MoPLauncher.Config;

// What a fresh install starts with. Change these before publishing a release
// if you want the launcher you hand out to point at your server already.
public static class ServerDefaults
{
    // IP or hostname of the login server, optionally with ":port".
    public const string ServerAddress = "127.0.0.1";

    // Shown under the title, e.g. "My Realm". Leave empty to show the address.
    public const string RealmName = "";

    // SHA-256 of the feedback upload API's self-signed certificate (printed by
    // the server repository's tools/feedback/make_cert.sh). The launcher sends
    // bug report screenshots, crash logs and Lua errors only to a server that
    // shows this certificate. Empty turns feedback uploads off.
    public const string FeedbackCertificate = "8A1E0EBE43E0B318015E0601391ED1EE7B008A049C8B828F14F288687637C8A6";
}
