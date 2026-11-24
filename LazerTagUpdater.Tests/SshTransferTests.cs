using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using FxSsh;
using FxSsh.Services;
using Xunit;
using Xunit.Abstractions;

namespace LazerTagUpdater.Tests;

public class SshTransferTests(ITestOutputHelper log)
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task SendsExactReleaseOverSshAndWaitsForTheInstaller(bool useKey, bool withHelper)
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            string file = Path.Combine(directory.FullName, "arena test release.tar.gz");
            string? supplied = Environment.GetEnvironmentVariable("LAZERTAG_TEST_RELEASE");
            if (string.IsNullOrEmpty(supplied))
            {
                using var fixture = UpdateTests.MakeRelease();
                await File.WriteAllBytesAsync(file, fixture.ToArray());
            }
            else File.Copy(supplied, file);
            // The built-in release checker is installed whether or not the folder has a copy.
            string script;
            using (var source = typeof(PiUpdate).Assembly.GetManifestResourceStream("LazerTagUpdater.deploy.sh")!)
            using (var reader = new StreamReader(source))
                script = (await reader.ReadToEndAsync()).ReplaceLineEndings("\n");
            byte[] helper = Encoding.UTF8.GetBytes(script);
            if (withHelper)
                await File.WriteAllTextAsync(Path.Combine(directory.FullName, PiUpdate.HelperFileName), script);
            using var expected = File.OpenRead(file);
            string hash = Convert.ToHexString(SHA256.HashData(expected)).ToLowerInvariant();
            string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            string clientPassword = useKey ? "" : password;
            if (withHelper)
            {
                byte[] login = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
                    { Host = "127.0.0.1", Username = "testuser", Password = password });
                await File.WriteAllBytesAsync(Path.Combine(directory.FullName, SavedPiLogin.FileName),
                    ProtectedData.Protect(login, null, DataProtectionScope.LocalMachine));
                clientPassword = Assert.IsType<SavedPiLogin>(SavedPiLogin.Load(directory.FullName))
                    .PasswordFor("127.0.0.1", "testuser");
            }
            string passphrase = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            string keyPath = "";
            byte[] publicKey = [];
            if (useKey)
            {
                keyPath = Path.Combine(directory.FullName, "test-key.pem");
                using var rsa = RSA.Create(2048);
                await File.WriteAllTextAsync(keyPath, rsa.ExportEncryptedPkcs8PrivateKeyPem(passphrase,
                    new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 10_000)));
                using var key = new Renci.SshNet.PrivateKeyFile(keyPath, passphrase);
                publicKey = key.HostKeyAlgorithms.First().Data;
            }
            const string stage = "/tmp/lazertag-upload.Ab34567890";
            var commands = new List<string>();
            bool receivedArchive = false, sudoInputOk = false, receivedHelper = false, helperInstalled = false;
            using var portProbe = new TcpListener(IPAddress.Loopback, 0);
            portProbe.Start();
            int port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
            portProbe.Stop();
            using var ssh = new SshServer(new StartingInfo(IPAddress.Loopback, port, "SSH-2.0-ArenaTest"));
            ssh.ExceptionRaised += (_, error) => log.WriteLine(error.ToString());
            using var hostKey = RSA.Create(2048);
            ssh.AddHostKey("rsa-sha2-256", hostKey.ExportRSAPrivateKeyPem());
            ssh.ConnectionAccepted += (_, session) => session.ServiceRegistered += (_, service) =>
            {
                if (service is UserAuthService auth)
                    auth.UserAuth += (_, request) => request.Result = request.Username == "testuser" && (useKey
                        ? request.AuthMethod == "publickey" && request.Key.SequenceEqual(publicKey)
                        : request.AuthMethod == "password" && request.Password == password);
                if (service is ConnectionService connection)
                    connection.CommandOpened += (_, request) =>
                    {
                        string command = request.CommandText;
                        commands.Add(command);
                        request.Agreed = request.ShellType == "exec";
                        var bytes = new MemoryStream();
                        request.Channel.DataReceived += (_, block) => bytes.Write(block.Span);
                        void Complete()
                        {
                            using (bytes)
                            {
                                string output = "";
                                uint exit = 0;
                                if (command == PiUpdate.CreateStage) output = stage;
                                else if (command == PiUpdate.ReadState) output = "{\"state\":\"Idle\"}";
                                else if (command.StartsWith($"umask 077; if cat > '{stage}/deploy.sh'"))
                                {
                                    receivedHelper = receivedArchive && bytes.ToArray().SequenceEqual(helper)
                                        && command.Contains(Convert.ToHexString(SHA256.HashData(helper)).ToLowerInvariant());
                                    if (!receivedHelper) exit = 1;
                                }
                                else if (command.StartsWith("umask"))
                                {
                                    bytes.Position = 0;
                                    receivedArchive = bytes.Length == expected.Length
                                        && Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() == hash
                                        && command.Contains(hash) && command.Contains(stage + "/release.tar.gz");
                                    if (!receivedArchive) exit = 1;
                                }
                                else if (command.StartsWith("sudo") && command.Contains("/bin/sh -ec"))
                                {
                                    bool helperSudoOk = useKey ? bytes.Length == 0 && command.StartsWith("sudo -n")
                                        : Encoding.UTF8.GetString(bytes.ToArray()) == password + "\n";
                                    helperInstalled = receivedHelper && helperSudoOk
                                        && command.Contains("/usr/local/sbin/lazertag-deploy")
                                        && command.Contains($"sh '{stage}/deploy.sh'");
                                    if (!helperInstalled) exit = 1;
                                }
                                else if (command.StartsWith("sudo"))
                                {
                                    sudoInputOk = useKey ? bytes.Length == 0 && command.StartsWith("sudo -n")
                                        : Encoding.UTF8.GetString(bytes.ToArray()) == password + "\n";
                                    string expectedCommand = $"/usr/local/sbin/lazertag-deploy '{stage}/release.tar.gz' --sha256 '{hash}'";
                                    if (!receivedArchive || !sudoInputOk || !helperInstalled || !command.Contains("systemd-run --wait")
                                        || !command.EndsWith(expectedCommand)) exit = 1;
                                }
                                else if (!command.StartsWith("test -x") && !command.StartsWith("rm -f")) exit = 1;
                                request.Channel.SendData(Encoding.UTF8.GetBytes(output));
                                request.Channel.SendClose(exit);
                            }
                        }
                        if (command.StartsWith("umask") || command.StartsWith("sudo -S"))
                            request.Channel.EofReceived += (_, _) => Complete();
                        else
                            Complete();
                    };
            };
            ssh.Start();
            var progress = new UpdateTests.RecordedProgress();
            bool prompted = false, unlocked = false;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            await PiUpdate.InstallAsync("127.0.0.1", port, "testuser", clientPassword, keyPath, file,
                new TrustedHosts(directory.FullName), (_, fingerprint) => prompted = fingerprint.StartsWith("SHA256:"),
                () => { unlocked = true; return passphrase; }, progress, deadline.Token).WaitAsync(TimeSpan.FromSeconds(50));
            Assert.True(prompted && receivedArchive && sudoInputOk);
            Assert.True(receivedHelper && helperInstalled);
            Assert.Equal(useKey, unlocked);
            Assert.DoesNotContain(commands, command => command.Contains(password));
            Assert.StartsWith("rm -f", commands[^1]);
            Assert.Contains(progress.Values, p => p.Percent == 100);
            Assert.True(progress.Values[^1].Installing);
        }
        finally { directory.Delete(true); }
    }
}
