using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace LazerTagUpdater;

internal record UpdateProgress(string Message, int? Percent = null, bool Installing = false);
internal record CommandResult(int? ExitCode, string Output, string Error)
{
    public void Check(string action)
    {
        if (ExitCode != 0)
            throw new InvalidOperationException($"{action}\n{Error.Trim()}\n{Output.Trim()}".Trim());
    }
}

internal delegate Task<CommandResult> RemoteCommand(string command, CancellationToken token,
    Stream? input = null, Action<long>? bytesSent = null);

internal static class PiUpdate
{
    internal const string HelperFileName = "lazertag-deploy-update.sh";
    internal const string CreateStage = "mktemp -d /tmp/lazertag-upload.XXXXXXXXXX";
    private const string CheckInstaller = "test -x /usr/local/sbin/lazertag-deploy";
    internal const string ReadState = "if systemctl is-active --quiet lazertag.service; then " +
        "curl -fsS --max-time 5 http://127.0.0.1:5000/api/game/state; else echo '{\"state\":\"Idle\"}'; fi";

    // Verify the root-owned copy before making it executable and swapping it into place.
    private const string ReplaceHelper = """
        destination=/usr/local/sbin/lazertag-deploy
        command -v python3 >/dev/null || { echo "Install python3 on the Pi before updating the release checker." >&2; exit 1; }
        tmp=$(mktemp /usr/local/sbin/.lazertag-deploy.XXXXXXXXXX)
        trap "rm -f -- $tmp" EXIT
        install -o root -g root -m 0600 -- "$1" "$tmp"
        printf "%s  %s\n" "$2" "$tmp" | sha256sum --check --status
        if ! cmp -s -- "$tmp" "$destination"; then
            chmod 0755 "$tmp"
            mv -Tf -- "$tmp" "$destination"
        fi
        """;

    internal static async Task InstallAsync(string host, int port, string username, string password,
        string keyPath, string archivePath, TrustedHosts knownHosts, Func<string, string, bool> trustNewKey,
        Func<string> keyPassphrase, IProgress<UpdateProgress> progress, CancellationToken token)
    {
        progress.Report(new("Checking the Linux ARM64 release…"));
        using var archive = File.OpenRead(archivePath);
        string digest = ReleaseArchive.CheckAndHash(archive, token);
        byte[]? helper = ReadCompanionHelper(archivePath);
        using var key = OpenKey(keyPath, keyPassphrase);
        using AuthenticationMethod auth = key == null
            ? new PasswordAuthenticationMethod(username, password)
            : new PrivateKeyAuthenticationMethod(username, key);
        var connection = new ConnectionInfo(host, port, username, auth) { Timeout = TimeSpan.FromSeconds(30) };
        using var client = new SshClient(connection) { KeepAliveInterval = TimeSpan.FromSeconds(10) };
        Exception? verificationError = null;
        client.HostKeyReceived += (_, e) =>
        {
            e.CanTrust = false;
            try
            {
                string fingerprint = "SHA256:" + e.FingerPrintSHA256;
                e.CanTrust = knownHosts.Verify(host, port, fingerprint, () => trustNewKey(e.HostKeyName, fingerprint));
                if (!e.CanTrust) verificationError = new OperationCanceledException("Pi identity confirmation cancelled.");
            }
            catch (Exception ex) { verificationError = ex; }
        };
        progress.Report(new($"Connecting to {host}…"));
        try { await client.ConnectAsync(token); }
        catch (Exception) when (verificationError != null) { throw verificationError; }

        Task<CommandResult> Run(string command, CancellationToken cancellation, Stream? input, Action<long>? bytesSent)
            => RunCommandAsync(client, command, cancellation, input, bytesSent);
        await DeployAsync(Run, archive, digest, password, progress, token, helper);
    }

    internal static byte[]? ReadCompanionHelper(string archivePath)
    {
        string path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(archivePath))!, HelperFileName);
        if (!File.Exists(path)) return null;
        using var bundled = typeof(PiUpdate).Assembly.GetManifestResourceStream("LazerTagUpdater.deploy.sh")!;
        using var reader = new StreamReader(bundled);
        string script = reader.ReadToEnd().ReplaceLineEndings("\n");
        if (File.ReadAllText(path).ReplaceLineEndings("\n") != script)
            throw new InvalidDataException("Choose the release checker and Windows updater from the same install kit.");
        return Encoding.UTF8.GetBytes(script);
    }

    private static PrivateKeyFile? OpenKey(string path, Func<string> passphrase)
    {
        if (path.Length == 0) return null;
        // Encrypted PKCS#8 attempts to decrypt even with an empty passphrase.
        if (File.ReadLines(path).FirstOrDefault()?.Trim() == "-----BEGIN ENCRYPTED PRIVATE KEY-----")
            return new PrivateKeyFile(path, passphrase());
        try { return new PrivateKeyFile(path); }
        catch (SshPassPhraseNullOrEmptyException) { return new PrivateKeyFile(path, passphrase()); }
    }

    private static async Task<CommandResult> RunCommandAsync(SshClient client, string command,
        CancellationToken token, Stream? input, Action<long>? bytesSent)
    {
        using var channel = client.CreateCommand(command);
        channel.CommandTimeout = TimeSpan.FromMinutes(15);
        var completion = channel.ExecuteAsync(token);
        IOException? writeError = null;
        try
        {
            if (input != null)
            {
                using var output = channel.CreateInputStream();
                if (bytesSent == null)
                {
                    await input.CopyToAsync(output, token);
                }
                else
                {
                    byte[] buffer = new byte[64 * 1024];
                    int count;
                    while ((count = await input.ReadAsync(buffer, token)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, count), token);
                        bytesSent(input.Position);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SshException or InvalidOperationException)
        {
            // A remote command can exit early, for example when the Pi's disk is full.
            writeError = new IOException("The Pi closed the transfer before it completed.", ex);
        }
        await completion;
        if (writeError != null && channel.ExitStatus == 0 && bytesSent != null) throw writeError;
        return new(channel.ExitStatus, channel.Result, channel.Error);
    }

    internal static async Task DeployAsync(RemoteCommand run, Stream archive, string digest, string password,
        IProgress<UpdateProgress> progress, CancellationToken token, byte[]? helper = null)
    {
        (await run(CheckInstaller, token)).Check("Install the arena software on the Pi before applying a release.");
        await CheckBetweenGames(run, token);
        var stage = await run(CreateStage, token);
        stage.Check("The Pi could not create a temporary upload directory.");
        string directory = stage.Output.Trim();
        if (!Regex.IsMatch(directory, @"\A/tmp/lazertag-upload\.[A-Za-z0-9]{10}\z")
            || !Regex.IsMatch(digest, @"\A[0-9a-f]{64}\z"))
            throw new InvalidDataException("Invalid upload directory or SHA-256 checksum.");
        string file = directory + "/release.tar.gz";
        string helperFile = directory + "/deploy.sh";
        string cleanup = $"rm -f -- '{file}' '{helperFile}' && rmdir -- '{directory}'";
        async Task Upload(Stream content, string target, string hash, string label, Action<long>? reportBytes = null)
        {
            string command = $"umask 077; if cat > '{target}' && printf '%s  %s\\n' '{hash}' '{target}' | sha256sum --check --status; " +
                $"then exit 0; else {cleanup}; echo 'Upload or SHA-256 verification failed.' >&2; exit 1; fi";
            (await run(command, token, content, reportBytes)).Check($"The Pi could not verify the uploaded {label}.");
        }

        bool releaseMayBeRunning = false;
        try
        {
            int lastPercent = -1;
            void ReportBytes(long sent)
            {
                int percent = (int)(100 * sent / archive.Length);
                if (percent == lastPercent) return;
                lastPercent = percent;
                progress.Report(new($"Uploading release · {percent}%", percent));
            }
            ReportBytes(0);
            await Upload(archive, file, digest, "release", ReportBytes);
            string? helperDigest = null;
            if (helper != null)
            {
                progress.Report(new("Uploading the Pi release checker…"));
                helperDigest = Convert.ToHexString(SHA256.HashData(helper)).ToLowerInvariant();
                using var content = new MemoryStream(helper);
                await Upload(content, helperFile, helperDigest, "release checker");
            }
            await CheckBetweenGames(run, token);
            token.ThrowIfCancellationRequested();
            progress.Report(new("Installing on the Pi and waiting for the arena health check…", Installing: true));

            // The system service continues even if the workstation disconnects during the restart.
            string unit = "lazertag-update-" + Guid.NewGuid().ToString("N");
            string install = $"/usr/bin/systemd-run --wait --collect --service-type=exec --unit={unit} " +
                $"/usr/local/sbin/lazertag-deploy '{file}'";
            if (helperDigest != null) install += $" --sha256 '{digest}'";
            string journal = $"journalctl -u {unit}.service -n 20 --no-pager -o cat";
            if (helperDigest != null)
            {
                string replace = $"/bin/sh -ec '{ReplaceHelper.ReplaceLineEndings("\n")}' sh '{helperFile}' '{helperDigest}'";
                (await RunSudoAsync(run, replace, password)).Check("The Pi could not install the release checker.");
                await CheckBetweenGames(run, CancellationToken.None);
            }
            CommandResult result;
            try
            {
                releaseMayBeRunning = true;
                result = await RunSudoAsync(run, install, password);
                if (result.ExitCode == null) throw new IOException("The SSH session ended without an exit status.");
                releaseMayBeRunning = false;
            }
            catch (Exception ex)
            {
                throw new IOException($"The install result is unknown. Check the update log on the Pi before retrying.\n{journal}", ex);
            }
            if (result.ExitCode != 0)
            {
                var log = await run(journal, CancellationToken.None);
                result.Check($"Installation failed. {log.Output.Trim()}");
            }
        }
        finally
        {
            if (!releaseMayBeRunning)
            {
                // /tmp also expires abandoned staging files after a connection loss.
                try { await run(cleanup, CancellationToken.None); }
                catch (Exception ex) when (ex is SshException or IOException or InvalidOperationException) { }
            }
        }
    }

    private static async Task<CommandResult> RunSudoAsync(RemoteCommand run, string command, string password)
    {
        if (password.Length == 0) return await run("sudo -n -- " + command, CancellationToken.None);
        byte[] credential = Encoding.UTF8.GetBytes(password + "\n");
        try
        {
            using var input = new MemoryStream(credential);
            return await run("sudo -S -p '' -- " + command, CancellationToken.None, input);
        }
        finally { CryptographicOperations.ZeroMemory(credential); }
    }

    private static async Task CheckBetweenGames(RemoteCommand run, CancellationToken token)
    {
        var snapshot = await run(ReadState, token);
        snapshot.Check("The Pi could not read the current game state.");
        using var document = JsonDocument.Parse(snapshot.Output);
        var root = document.RootElement;
        string? state = null;
        if (root.TryGetProperty("hostingState", out var hosting) || root.TryGetProperty("HostingState", out hosting))
            state = hosting.GetString();
        if (string.IsNullOrWhiteSpace(state) && (root.TryGetProperty("state", out var legacy) || root.TryGetProperty("State", out legacy)))
            state = legacy.GetString();
        if (state is not ("Idle" or "GameOver" or "Game Over"))
            throw new InvalidOperationException($"The arena reports {state ?? "an unknown state"}. Finish the game and debrief before installing an update.");
    }
}
