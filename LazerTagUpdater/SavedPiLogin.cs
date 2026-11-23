using System.Security.Cryptography;
using System.Text.Json;

namespace LazerTagUpdater;

internal sealed class SavedPiLogin
{
    internal const string FileName = "pi-login.dpapi";
    public string Host { get; init; } = "";
    public string Username { get; init; } = "";
    public string Password { get; init; } = "";

    internal string PasswordFor(string host, string username) =>
        string.Equals(Host.TrimEnd('.'), host.Trim().TrimEnd('.'), StringComparison.OrdinalIgnoreCase)
        && Username == username.Trim() ? Password : "";

    internal static SavedPiLogin? Load(string directory)
    {
        string path = Path.Combine(directory, FileName);
        if (!File.Exists(path)) return null;
        try
        {
            byte[] clear = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.LocalMachine);
            try
            {
                var login = JsonSerializer.Deserialize<SavedPiLogin>(clear);
                return login != null && !string.IsNullOrWhiteSpace(login.Host) &&
                    !string.IsNullOrWhiteSpace(login.Username) && !string.IsNullOrEmpty(login.Password) ? login : null;
            }
            finally { CryptographicOperations.ZeroMemory(clear); }
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException or UnauthorizedAccessException)
        {
            // A portable kit can still use an entered password on a different PC.
            return null;
        }
    }
}
