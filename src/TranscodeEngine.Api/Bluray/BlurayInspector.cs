using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TranscodeEngine.Api.Transcoding;
using TranscodeEngine.Api.Probing;
using Microsoft.Extensions.Logging.Abstractions;

namespace TranscodeEngine.Api.Bluray;

/// <summary>Reads playlist structure cheaply; delegates selected-track discovery to MKVToolNix.</summary>
public sealed class BlurayInspector(TranscodeEngineSettings settings) : IBlurayInspector
{
    public async Task<BlurayInspection> InspectAsync(string root, string? playlistId, CancellationToken cancellationToken)
    {
        var inventory = Inventory(root);
        var playlistDirectory = Child(Child(root, "BDMV"), "PLAYLIST");
        var playlists = new List<BlurayPlaylist>();
        foreach (var file in Directory.EnumerateFiles(playlistDirectory).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Path.GetExtension(file).Equals(".mpls", StringComparison.OrdinalIgnoreCase)) continue;
            var id = Path.GetFileNameWithoutExtension(file);
            if (!ValidId(id)) continue;
            try
            {
                if (new FileInfo(file).Length > 4 * 1024 * 1024) throw new ArgumentException("Playlist is too large.");
                var playlist = ParsePlaylist(id, await File.ReadAllBytesAsync(file, cancellationToken));
                var streams = Child(Child(root, "BDMV"), "STREAM");
                foreach (var clip in playlist.Clips) _ = Child(streams, clip);
                if (playlistId == id)
                {
                    var json = await IdentifyAsync(settings.MkvmergePath, file, cancellationToken);
                    var formats = new List<ProbedStreamInfo>();
                    var probe = new FfprobeMediaInspector(settings, NullLogger<FfprobeMediaInspector>.Instance);
                    foreach (var clip in playlist.Clips.Distinct())
                    {
                        var media = await probe.InspectAsync(Child(streams, clip), cancellationToken);
                        var video = media?.Streams.FirstOrDefault(s => s.Kind == ProbedStreamKind.Video);
                        if (video is null) throw new ArgumentException("The selected clip has no readable picture; check protection or missing data.");
                        formats.Add(video);
                    }
                    var tracks = ParseTracks(json).ToArray();
                    var picture = Array.FindIndex(tracks, t => t.Type == "video");
                    if (picture >= 0 && formats.FirstOrDefault()?.DolbyVision is { } dv)
                        tracks[picture] = tracks[picture] with { DolbyVision = JsonSerializer.Serialize(dv) };
                    playlist = playlist with { Tracks = tracks, VideoFormats = formats };
                }
                playlists.Add(playlist);
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or JsonException)
            {
                playlists.Add(new(id, 0, 0, [], [], exception.Message));
            }
        }
        if (playlists.Count == 0) throw new ArgumentException("The disc has no readable playlist files.");
        if (playlistId is not null && !playlists.Any(p => p.Id == playlistId))
            throw new ArgumentException("The selected playlist does not exist.");
        if (Inventory(root).Revision != inventory.Revision)
            throw new ArgumentException("The disc changed during inspection. Inspect it again.");
        return new(inventory.Revision, inventory.Size, playlists);
    }

    /// <summary>Reject links throughout the owned tree and hash member identity/size/time for stale-input detection.</summary>
    internal static (string Revision, long Size) Inventory(string root)
    {
        root = Path.GetFullPath(root);
        for (DirectoryInfo? entry = new(root); entry is not null; entry = entry.Parent)
            if (entry.LinkTarget is not null) throw new ArgumentException("Disc paths cannot contain symbolic links.");
        if (!Directory.Exists(root)) throw new ArgumentException("The Blu-ray directory is missing.");
        _ = Child(Child(root, "BDMV"), "index.bdmv");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long size = 0;
        var queue = new Stack<string>();
        queue.Push(root);
        var count = 0;
        while (queue.TryPop(out var directory))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
            {
                if (++count > 100000) throw new ArgumentException("The disc contains too many members.");
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new ArgumentException("Disc members cannot be symbolic links.");
                if ((attributes & FileAttributes.Directory) != 0) { queue.Push(path); continue; }
                var info = new FileInfo(path);
                size = checked(size + info.Length);
                hash.AppendData(Encoding.UTF8.GetBytes($"{Path.GetRelativePath(root, path)}\0{info.Length}\0{info.LastWriteTimeUtc.Ticks}\n"));
            }
        }
        return (Convert.ToHexString(hash.GetHashAndReset()), size);
    }

    internal static string Child(string parent, string name) => ResolveChild(Directory.EnumerateFileSystemEntries(parent), name);

    internal static string ResolveChild(IEnumerable<string> entries, string name)
    {
        var matches = entries.Where(path => Path.GetFileName(path).Equals(name, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        return matches.Length switch
        {
            0 => throw new ArgumentException($"A required disc member is missing: {name}."),
            1 => matches[0],
            _ => throw new ArgumentException($"Ambiguous disc member: multiple entries match {name} ignoring case.")
        };
    }
    internal static bool ValidId(string id) => id.Length == 5 && id.All(char.IsAsciiDigit);
    internal static string PlaylistPath(string root, string id) => ValidId(id)
        ? Child(Child(Child(root, "BDMV"), "PLAYLIST"), id + ".mpls")
        : throw new ArgumentException("A playlist id must contain five digits.");

    internal static BlurayPlaylist ParsePlaylist(string id, byte[] bytes)
    {
        try
        {
            if (bytes.Length < 20 || !bytes.AsSpan(0, 4).SequenceEqual("MPLS"u8))
                throw new ArgumentException("Invalid Blu-ray playlist header.");
            var offset = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8)));
            var marks = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(12)));
            var count = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset + 6));
            var position = offset + 10;
            long ticks = 0;
            var clips = new List<string>();
            for (var i = 0; i < count; i++)
            {
                var length = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(position));
                position += 2;
                if (length < 20 || position + length > bytes.Length) throw new ArgumentException("Truncated playlist item.");
                var clip = Encoding.ASCII.GetString(bytes, position, 5);
                if (!ValidId(clip) || !bytes.AsSpan(position + 5, 4).SequenceEqual("M2TS"u8))
                    throw new ArgumentException("Unsupported playlist clip reference.");
                if ((BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(position + 9)) & 0x10) != 0)
                    throw new ArgumentException("Multi-angle playlists are not supported.");
                var start = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(position + 12));
                var end = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(position + 16));
                if (end <= start) throw new ArgumentException("Invalid playlist clip timing.");
                ticks += end - start;
                clips.Add(clip + ".m2ts");
                position += length;
            }
            var chapters = 0;
            if (marks > 0)
            {
                var markCount = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(marks + 4));
                for (var i = 0; i < markCount; i++)
                    if (bytes[marks + 6 + i * 14 + 1] == 1) chapters++;
            }
            if (clips.Count == 0) throw new ArgumentException("The playlist has no video clips.");
            return new(id, ticks / 45000d, chapters, clips, []);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or OverflowException)
        { throw new ArgumentException("The playlist is truncated or malformed.", ex); }
    }

    internal static IReadOnlyList<BlurayTrack> ParseTracks(string json)
    {
        using var document = JsonDocument.Parse(json.Trim().TrimStart('\uFEFF'));
        var root = document.RootElement;
        if (root.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0)
            throw new ArgumentException(string.Join("; ", errors.EnumerateArray().Select(e => e.GetString())));
        if (!root.TryGetProperty("tracks", out var tracks)) throw new ArgumentException("The playlist has no readable tracks; check disc protection or missing data.");
        return tracks.EnumerateArray().Select(t =>
        {
            var p = t.GetProperty("properties");
            string? S(string name) => p.TryGetProperty(name, out var v) ? v.ToString() : null;
            bool B(string name) => p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
            return new BlurayTrack(t.GetProperty("id").GetInt32(), t.GetProperty("type").GetString()!,
                t.GetProperty("codec").GetString()!, S("language_ietf") ?? S("language"), S("track_name"),
                B("default_track"), B("forced_track"), S("pixel_dimensions"),
                p.TryGetProperty("audio_channels", out var channels) ? channels.GetInt32() : null,
                null);
        }).ToList();
    }

    internal static async Task<string> IdentifyAsync(string tool, string path, CancellationToken ct, TimeSpan? inspectionTimeout = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(inspectionTimeout ?? TimeSpan.FromMinutes(2));
        var start = FfmpegTranscodeEngine.ToolProcess(tool);
        foreach (var arg in FfmpegTranscodeEngine.BuildIdentifyArguments(path)) start.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new ArgumentException("Could not start disc inspection.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        { throw new ArgumentException("Could not start MKVToolNix. Check that mkvmerge is installed and executable.", ex); }
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var text = await output;
            var detail = await error;
            if (process.ExitCode > 1) throw new ArgumentException($"Disc inspection failed: {detail} {text}");
            return text;
        }
        catch (Exception ex)
        {
            try { if (!process.HasExited) process.Kill(true); }
            catch (Exception killError) when (killError is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            if (ex is OperationCanceledException && !ct.IsCancellationRequested)
                throw new ArgumentException("MKVToolNix disc inspection timed out. Retry inspection or check the source storage.", ex);
            throw;
        }
    }
}
