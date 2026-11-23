using System.Buffers.Binary;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;

namespace LazerTagUpdater;

internal static class ReleaseArchive
{
    public static string CheckAndHash(Stream file, CancellationToken cancellation)
    {
        var required = new HashSet<string>(StringComparer.Ordinal)
        {
            "LazerTagWebServer", "LazerTagWebServer.dll", "LazerTagWebServer.deps.json",
            "LazerTagWebServer.runtimeconfig.json", "GameTypes.xml", "wwwroot/index.html",
            "wwwroot/tv.html", "wwwroot/css/app.css", "wwwroot/js/app.js", "wwwroot/js/signalr.min.js"
        };
        bool needsSnapshotScript = false, hasSnapshotScript = false;
        using (var gzip = new GZipStream(file, CompressionMode.Decompress, leaveOpen: true))
        using (var tar = new TarReader(gzip))
        {
            var files = new HashSet<string>(StringComparer.Ordinal);
            var directories = new HashSet<string>(StringComparer.Ordinal) { "" };
            TarEntry? entry;
            while ((entry = tar.GetNextEntry()) != null)
            {
                cancellation.ThrowIfCancellationRequested();
                if (entry.EntryType == TarEntryType.GlobalExtendedAttributes) continue;
                string name = RegisterMember(entry, files, directories);
                if (entry.EntryType == TarEntryType.Directory) continue;
                if (required.Contains(name) && entry.Length == 0)
                    throw new InvalidDataException($"The release contains an empty {name}.");
                if (name == "wwwroot/js/snapshots.js") hasSnapshotScript = entry.Length > 0;
                if (name is "wwwroot/index.html" or "wwwroot/tv.html")
                    needsSnapshotScript |= ReferencesSnapshotScript(entry.DataStream!);
                if (name == "GameTypes.xml")
                {
                    try
                    {
                        using var reader = XmlReader.Create(entry.DataStream!);
                        var catalog = XDocument.Load(reader).Root;
                        if (catalog?.Name != "LazerSwarm" ||
                            catalog.Element("FileInfo")?.Element("FileType")?.Value != "GameTypes" ||
                            catalog.Element("GameTypes")?.Elements("GameType")
                                .Any(mode => (string?)mode.Attribute("Name") == "2TMS") != true)
                            throw new InvalidDataException("GameTypes.xml is missing the game catalog or its default mode.");
                    }
                    catch (XmlException ex)
                    {
                        throw new InvalidDataException("GameTypes.xml is malformed.", ex);
                    }
                }
                if (name == "LazerTagWebServer")
                {
                    byte[] header = new byte[20];
                    if (entry.DataStream == null || entry.DataStream.ReadAtLeast(header, header.Length, false) < header.Length
                        || !header.AsSpan(0, 4).SequenceEqual("\u007fELF"u8) || header[4] != 2 || header[5] != 1
                        || BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(18)) != 183)
                        throw new InvalidDataException("The server executable must be built for Linux ARM64.");
                }
                required.Remove(name);
            }
        }
        if (needsSnapshotScript && !hasSnapshotScript) required.Add("wwwroot/js/snapshots.js");
        if (required.Count > 0)
            throw new InvalidDataException($"Choose the server release archive. Missing {string.Join(", ", required)}.");
        cancellation.ThrowIfCancellationRequested();
        file.Position = 0;
        string hash = Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
        file.Position = 0;
        return hash;
    }

    private static bool ReferencesSnapshotScript(Stream page)
    {
        ReadOnlySpan<byte> name = "/js/snapshots.js"u8;
        byte[] buffer = new byte[4096];
        int kept = 0, read;
        while ((read = page.Read(buffer, kept, buffer.Length - kept)) > 0)
        {
            int length = kept + read;
            if (buffer.AsSpan(0, length).IndexOf(name) >= 0) return true;
            kept = Math.Min(length, name.Length - 1);
            buffer.AsSpan(length - kept, kept).CopyTo(buffer);
        }
        return false;
    }

    private static string RegisterMember(TarEntry entry, HashSet<string> files, HashSet<string> directories)
    {
        if (entry.EntryType is not (TarEntryType.Directory or TarEntryType.RegularFile or TarEntryType.V7RegularFile))
            throw new InvalidDataException("The release must contain only regular files and directories.");
        string raw = entry.Name;
        var parts = raw.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(part => part != ".").ToArray();
        string name = string.Join('/', parts);
        if (raw.StartsWith('/') || raw.Contains('\\') || raw.Contains('\0') || parts.Contains("..")
            || (name.Length >= 2 && char.IsAsciiLetter(name[0]) && name[1] == ':'))
            throw new InvalidDataException("The release contains an invalid member name.");

        for (int slash = name.IndexOf('/'); slash >= 0; slash = name.IndexOf('/', slash + 1))
        {
            string directory = name[..slash];
            if (files.Contains(directory)) throw new InvalidDataException("A member conflicts with a file.");
            directories.Add(directory);
        }
        if (files.Contains(name) || (entry.EntryType != TarEntryType.Directory && directories.Contains(name)))
            throw new InvalidDataException("The release contains conflicting member paths.");
        if (entry.EntryType == TarEntryType.Directory) directories.Add(name);
        else files.Add(name);
        return name;
    }
}
