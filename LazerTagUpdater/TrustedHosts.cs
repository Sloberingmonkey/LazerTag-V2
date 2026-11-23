using System.Security.Cryptography;
using System.Text;

namespace LazerTagUpdater;

internal sealed class TrustedHosts(string directory)
{
    public bool Verify(string host, int port, string fingerprint, Func<bool> approve)
    {
        string identity = $"{host.TrimEnd('.').ToLowerInvariant()}:{port}";
        string file = Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))) + ".txt");
        Directory.CreateDirectory(directory);
        if (!File.Exists(file))
        {
            if (!approve()) return false;
            string temporary = Path.Combine(directory, Path.GetRandomFileName());
            try
            {
                File.WriteAllText(temporary, fingerprint);
                try { File.Move(temporary, file); }
                catch (IOException) when (File.Exists(file)) { /* Another window saved the first pin. */ }
            }
            finally { File.Delete(temporary); }
        }
        if (File.ReadAllText(file) != fingerprint)
            throw new InvalidOperationException($"The SSH host key for {host} has changed. Verify the Pi's key before updating. Its saved fingerprint is in {file}.");
        return true;
    }
}
