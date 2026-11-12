using System.Security.Cryptography;
using System.Text;

namespace MoPLauncher.Config;

// Keeps the game password for auto-login. It is encrypted with Windows data
// protection (DPAPI) for the signed-in Windows user, so the text in
// config.json can't be read by another user or on another PC.
public static class SavedPassword
{
    public static string Protect(string password) =>
        Convert.ToBase64String(
            ProtectedData.Protect(Encoding.UTF8.GetBytes(password), null, DataProtectionScope.CurrentUser));

    // Null when nothing is saved or it can't be read back (a config file
    // copied from another PC or Windows account).
    public static string? Unprotect(string? saved)
    {
        if (string.IsNullOrEmpty(saved))
            return null;

        try
        {
            return Encoding.UTF8.GetString(
                ProtectedData.Unprotect(Convert.FromBase64String(saved), null, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }
}
