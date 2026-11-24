using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace LazerTagUpdater.Tests;

public class UpdateTests
{
    internal static MemoryStream MakeRelease(byte machine = 183, string? omitted = null, string? catalog = null,
        string? empty = null, Action<TarWriter>? extra = null)
    {
        var file = new MemoryStream();
        using (var gzip = new GZipStream(file, CompressionMode.Compress, true))
        using (var tar = new TarWriter(gzip))
        {
            void Add(string path, byte[] data)
            {
                if (path == omitted) return;
                using var stream = new MemoryStream(path == empty ? [] : data);
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "./" + path) { DataStream = stream });
            }
            byte[] elf = new byte[20];
            "\u007fELF"u8.CopyTo(elf);
            elf[4] = 2;
            elf[5] = 1;
            elf[18] = machine;
            Add("LazerTagWebServer", elf);
            Add("LazerTagWebServer.dll", RandomNumberGenerator.GetBytes(160_000));
            Add("LazerTagWebServer.runtimeconfig.json", "{}"u8.ToArray());
            Add("LazerTagWebServer.deps.json", "{}"u8.ToArray());
            Add("GameTypes.xml", catalog == null ? File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "GameTypes.xml"))
                : System.Text.Encoding.UTF8.GetBytes(catalog));
            foreach (string page in new[] { "index.html", "tv.html", "css/app.css", "js/app.js", "js/signalr.min.js" })
                Add("wwwroot/" + page, "asset"u8.ToArray());
            extra?.Invoke(tar);
        }
        file.Position = 0;
        return file;
    }

    [Fact]
    public void ChecksArchiveAndRewindsToUploadTheSameBytes()
    {
        using var file = MakeRelease();
        string expected = Convert.ToHexString(SHA256.HashData(file.ToArray())).ToLowerInvariant();
        Assert.Equal(expected, ReleaseArchive.CheckAndHash(file, default));
        Assert.Equal(0, file.Position);
    }

    [Fact]
    public void Release_pages_that_reference_snapshot_ordering_require_the_shared_script()
    {
        using var file = MakeRelease(omitted: "wwwroot/tv.html", extra: tar =>
        {
            using var html = new MemoryStream("<script src='/js/snapshots.js'></script>"u8.ToArray());
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "wwwroot/tv.html") { DataStream = html });
        });
        Assert.Contains("wwwroot/js/snapshots.js", Assert.Throws<InvalidDataException>(() => ReleaseArchive.CheckAndHash(file, default)).Message);
    }

    [Theory]
    [InlineData(62, null, "ARM64")]
    [InlineData(183, "wwwroot/index.html", "wwwroot/index.html")]
    [InlineData(183, "wwwroot/tv.html", "wwwroot/tv.html")]
    [InlineData(183, "wwwroot/css/app.css", "wwwroot/css/app.css")]
    [InlineData(183, "wwwroot/js/app.js", "wwwroot/js/app.js")]
    [InlineData(183, "wwwroot/js/signalr.min.js", "wwwroot/js/signalr.min.js")]
    [InlineData(183, "GameTypes.xml", "GameTypes.xml")]
    public void RejectsWrongArchitectureAndIncompleteReleases(byte machine, string? omitted, string detail)
    {
        using var file = MakeRelease(machine, omitted);
        Assert.Contains(detail, Assert.Throws<InvalidDataException>(() => ReleaseArchive.CheckAndHash(file, default)).Message);
    }

    [Theory]
    [InlineData("<")]
    [InlineData("<games/>")]
    public void RejectsUnusableCatalogBeforeUpload(string catalog)
    {
        using var file = MakeRelease(catalog: catalog);
        Assert.Contains("GameTypes.xml", Assert.Throws<InvalidDataException>(() => ReleaseArchive.CheckAndHash(file, default)).Message);
    }

    [Fact]
    public void RejectsEmptyTvPage()
    {
        using var file = MakeRelease(empty: "wwwroot/tv.html");
        Assert.Contains("wwwroot/tv.html", Assert.Throws<InvalidDataException>(() => ReleaseArchive.CheckAndHash(file, default)).Message);
    }

    [Theory]
    [InlineData(TarEntryType.SymbolicLink, "readme-link")]
    [InlineData(TarEntryType.HardLink, "readme-copy")]
    [InlineData(TarEntryType.Fifo, "queue")]
    [InlineData(TarEntryType.RegularFile, "./wwwroot/./index.html")]
    [InlineData(TarEntryType.RegularFile, "wwwroot")]
    [InlineData(TarEntryType.RegularFile, "bad\\name")]
    public void RejectsUnsupportedOrConflictingReleaseMembers(TarEntryType type, string name)
    {
        using var release = MakeRelease(extra: tar =>
        {
            var entry = new PaxTarEntry(type, name);
            if (type is TarEntryType.SymbolicLink or TarEntryType.HardLink) entry.LinkName = "GameTypes.xml";
            tar.WriteEntry(entry);
        });
        Assert.Throws<InvalidDataException>(() => ReleaseArchive.CheckAndHash(release, default));
    }

    [Fact]
    public void SavedLoginIsMatchedToThePiAndAccountAndCanFallBackToManualEntry()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            string path = Path.Combine(directory.FullName, SavedPiLogin.FileName);
            Assert.Null(SavedPiLogin.Load(directory.FullName));
            string secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)) + "#";
            byte[] json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
                { Host = "pi.local", Username = "owner", Password = secret });
            File.WriteAllBytes(path, ProtectedData.Protect(json, null, DataProtectionScope.LocalMachine));
            var login = Assert.IsType<SavedPiLogin>(SavedPiLogin.Load(directory.FullName));
            Assert.Equal(secret, login.PasswordFor("PI.local.", "owner"));
            Assert.Empty(login.PasswordFor("other.local", "owner"));
            Assert.Empty(login.PasswordFor("pi.local", "other"));
            File.WriteAllBytes(path, [1, 2, 3]);
            Assert.Null(SavedPiLogin.Load(directory.FullName));
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public void RejectsMismatchedCompanionHelperBeforeConnecting()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            string release = Path.Combine(directory.FullName, "release.tar.gz");
            // Without an adjacent copy, the built-in release checker is still installed.
            string builtIn = Encoding.UTF8.GetString(PiUpdate.ReadCompanionHelper(release));
            Assert.Contains("lazertag-deploy", builtIn);
            File.WriteAllText(Path.Combine(directory.FullName, PiUpdate.HelperFileName), builtIn);
            Assert.Equal(builtIn, Encoding.UTF8.GetString(PiUpdate.ReadCompanionHelper(release)));
            File.WriteAllText(Path.Combine(directory.FullName, PiUpdate.HelperFileName), "different version");
            Assert.Throws<InvalidDataException>(() => PiUpdate.ReadCompanionHelper(release));
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public void SavesOnlyApprovedHostKeysAndRejectsAChangedKey()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var pins = new TrustedHosts(directory.FullName);
            Assert.False(pins.Verify("pi.local", 22, "first", () => false));
            Assert.Empty(directory.GetFiles());
            Assert.True(pins.Verify("pi.local", 22, "first", () => true));
            bool UnexpectedPrompt() => throw new InvalidOperationException("duplicate key prompt");
            Assert.True(pins.Verify("PI.LOCAL.", 22, "first", UnexpectedPrompt));
            Assert.Contains("changed", Assert.Throws<InvalidOperationException>(
                () => pins.Verify("pi.local", 22, "second", UnexpectedPrompt)).Message);
        }
        finally { directory.Delete(true); }
    }

    [Theory]
    [InlineData("already-playing", false, false, "Playing")]
    [InlineData("game-starts-during-upload", true, false, "Countdown")]
    [InlineData("bad-checksum", true, false, "verify")]
    [InlineData("cancel-upload", true, false, "cancel")]
    [InlineData("deploy-rolled-back", true, true, "restored previous release")]
    [InlineData("connection-lost-after-install", true, true, "result is unknown")]
    public async Task InstallFailuresRespectTheActualTransferState(string scenario, bool uploaded, bool installed, string message)
    {
        using var archive = MakeRelease();
        string hash = ReleaseArchive.CheckAndHash(archive, default);
        using var stop = new CancellationTokenSource();
        bool sawUpload = false, sawInstall = false, cleaned = false;
        var progress = new RecordedProgress();
        const string stage = "/tmp/lazertag-upload.123456789A";
        Task<CommandResult> Run(string command, CancellationToken token, Stream? input, Action<long>? sent)
        {
            token.ThrowIfCancellationRequested();
            CommandResult result = new(0, "", "");
            if (command == PiUpdate.ReadState)
            {
                string state = scenario == "already-playing" ? "Playing"
                    : scenario == "game-starts-during-upload" && sawUpload ? "Countdown" : "Idle";
                result = new(0, "{\"hostingState\":\"" + state + "\"}", "");
            }
            else if (command == PiUpdate.CreateStage) result = new(0, stage, "");
            else if (command.StartsWith("umask"))
            {
                sawUpload = true;
                if (scenario == "bad-checksum") result = new(1, "", "checksum mismatch");
                if (scenario == "cancel-upload") stop.Cancel();
            }
            else if (command.StartsWith("sudo"))
            {
                sawInstall = true;
                if (scenario == "connection-lost-after-install") throw new IOException("lost session");
                result = new(1, "", "service failed");
            }
            else if (command.StartsWith("journalctl")) result = new(0, "restored previous release", "");
            else if (command.StartsWith("rm -f")) cleaned = true;
            return Task.FromResult(result);
        }
        var error = await Record.ExceptionAsync(() => PiUpdate.DeployAsync(Run, archive, hash, "", progress, stop.Token));
        Assert.NotNull(error);
        Assert.Contains(message, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(uploaded, sawUpload);
        Assert.Equal(installed, sawInstall);
        Assert.Equal(uploaded && scenario != "connection-lost-after-install", cleaned);
    }

    internal sealed class RecordedProgress : IProgress<UpdateProgress>
    {
        public List<UpdateProgress> Values { get; } = [];
        public void Report(UpdateProgress value) => Values.Add(value);
    }
}
