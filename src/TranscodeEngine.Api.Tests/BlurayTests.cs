using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using TranscodeEngine.Api.Probing;
using Microsoft.Extensions.Logging.Abstractions;
using TranscodeEngine.Api.Api;
using TranscodeEngine.Api.Bluray;
using TranscodeEngine.Api.Transcoding;
using Xunit;

namespace TranscodeEngine.Api.Tests;

public sealed class BlurayTests : IDisposable
{
    private readonly string root = Directory.CreateDirectory(Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "bluray-tests-" + Guid.NewGuid().ToString("N"))).FullName;
    public void Dispose() => Directory.Delete(root, true);

    private static byte[] Playlist(bool angle = false)
    {
        var bytes = new byte[140];
        Encoding.ASCII.GetBytes("MPLS0200").CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), 40);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(12), 100);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(46), 2);
        for (var i = 0; i < 2; i++)
        {
            var pos = 50 + i * 22;
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(pos), 20);
            Encoding.ASCII.GetBytes($"{i+1:00000}M2TS").CopyTo(bytes, pos + 2);
            if (angle) bytes[pos + 12] = 0x10;
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(pos + 14), 90000);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(pos + 18), 540000);
        }
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(104), 2);
        bytes[107] = 1; bytes[121] = 1;
        return bytes;
    }

    [Fact]
    public void Playlist_uses_in_out_times_across_clips_and_reads_chapters()
    {
        var playlist = BlurayInspector.ParsePlaylist("00001", Playlist());
        Assert.Equal(20, playlist.DurationSeconds);
        Assert.Equal(2, playlist.Chapters);
        Assert.Equal(new[] { "00001.m2ts", "00002.m2ts" }, playlist.Clips);
    }

    [Fact]
    public void Malformed_and_multi_angle_playlists_are_explicitly_refused()
    {
        Assert.Throws<ArgumentException>(() => BlurayInspector.ParsePlaylist("00001", Playlist()[..70]));
        Assert.Contains("Multi-angle", Assert.Throws<ArgumentException>(() => BlurayInspector.ParsePlaylist("00001", Playlist(true))).Message);
    }

    [Fact]
    public void Revision_detects_changed_members_and_links_are_never_followed()
    {
        Directory.CreateDirectory(Path.Combine(root, "BDMV"));
        File.WriteAllText(Path.Combine(root, "BDMV/index.bdmv"), "index");
        var first = BlurayInspector.Inventory(root);
        File.WriteAllText(Path.Combine(root, "BDMV/index.bdmv"), "changed index");
        Assert.NotEqual(first.Revision, BlurayInspector.Inventory(root).Revision);
        File.CreateSymbolicLink(Path.Combine(root, "BDMV/link"), Path.Combine(root, "BDMV/index.bdmv"));
        Assert.Throws<ArgumentException>(() => BlurayInspector.Inventory(root));
    }

    private static BluraySelection Selection => new("revision", "00001", 0,
        [new(2, "ru", "Dub", true)], [new(4, "en", "Forced", false, true)]);
    private static BlurayPlaylist Tracks => new("00001", 20, 2, ["00001.m2ts"], [
        new(0, "video", "HEVC", null, null, true, false, "3840x2160", null, null),
        new(2, "audio", "DTS-HD", "en", null, true, false, null, 8, null),
        new(4, "subtitles", "PGS", "en", null, false, false, null, null, null)]);

    [Fact]
    public void Selection_addresses_tracks_by_id_and_never_maps_a_secondary_picture()
    {
        FfmpegTranscodeEngine.ValidateBluraySelection(Selection, Tracks);
        Assert.Throws<ArgumentException>(() => FfmpegTranscodeEngine.ValidateBluraySelection(Selection with { VideoTrackId = 9 }, Tracks));
        Assert.Throws<ArgumentException>(() => FfmpegTranscodeEngine.ValidateBluraySelection(Selection with { Audio = [new(4)] }, Tracks));
        Assert.Throws<ArgumentException>(() => FfmpegTranscodeEngine.ValidateBluraySelection(Selection with { Audio = [new(2), new(2)] }, Tracks));
        var args = FfmpegTranscodeEngine.BuildBlurayArguments("/disc/00001.mpls", "/result.mkv", Selection);
        Assert.Equal("keep_last_chapter_in_mpls", args[args.IndexOf("--engage") + 1]);
        Assert.Equal("0", args[args.IndexOf("--video-tracks") + 1]);
        Assert.Equal("2", args[args.IndexOf("--audio-tracks") + 1]);
        Assert.Contains("2:ru", args); Assert.Contains("4:1", args);
        Assert.Equal("/disc/00001.mpls", args[^1]);
        Assert.Contains("--no-subtitles", FfmpegTranscodeEngine.BuildBlurayArguments("a", "b", Selection with { Subtitles = [] }));
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(1, false)]
    [InlineData(3, false)]
    public void ValidateBlurayOutput_ChapterCount_RequiresEveryPlaylistChapter(int chapterCount, bool accepted)
    {
        var json = JsonSerializer.Serialize(new
        {
            container = new { properties = new { duration = 20_000_000_000L } },
            chapters = new[] { new { num_entries = chapterCount } },
            tracks = Tracks.Tracks.Select(track => new
            {
                id = track.Id, type = track.Type, codec = track.Codec,
                properties = new Dictionary<string, object?>
                {
                    ["pixel_dimensions"] = track.PixelDimensions,
                    ["default_track"] = track.Default,
                    ["forced_track"] = track.Forced,
                }.Where(pair => pair.Value is not null).Concat(track.Channels is { } channels
                    ? new Dictionary<string, object?> { ["audio_channels"] = channels }
                    : []).ToDictionary(pair => pair.Key, pair => pair.Value)
            })
        });
        var selection = Selection with { Audio = [new(2, Default: true)], Subtitles = [new(4)] };

        if (accepted)
            FfmpegTranscodeEngine.ValidateBlurayOutput(Tracks, selection, json);
        else
        {
            var error = Assert.Throws<ArgumentException>(() => FfmpegTranscodeEngine.ValidateBlurayOutput(Tracks, selection, json));
            Assert.Contains($"expected 2, found {chapterCount}", error.Message);
        }
    }

    [Fact]
    public void Dolby_vision_validation_refuses_lost_enhancement_layer_or_hdr_changes()
    {
        var source = new ProbedStreamInfo(0, ProbedStreamKind.Video, "hevc", "Main 10", null, null, true, false,
            null, 3840, 2160, 23.976, 10, HdrFormat.DolbyVision, null, null, new(7, 6, 6, true, true, true));
        FfmpegTranscodeEngine.ValidateBlurayPicture([source], source);
        Assert.Throws<ArgumentException>(() => FfmpegTranscodeEngine.ValidateBlurayPicture([source], source with { DolbyVision = source.DolbyVision! with { ElPresent = false } }));
        Assert.Throws<ArgumentException>(() => FfmpegTranscodeEngine.ValidateBlurayPicture([source], source with { Hdr = HdrFormat.Hdr10, DolbyVision = null }));
        Assert.Throws<ArgumentException>(() => FfmpegTranscodeEngine.ValidateBlurayPicture([source], source with { BitDepth = 8 }));
        Assert.Throws<ArgumentException>(() => FfmpegTranscodeEngine.ValidateBlurayPicture([source], null));
    }

    [Fact]
    public async Task Restart_retains_an_interrupted_job_and_preserves_the_disc()
    {
        var id = Guid.NewGuid().ToString("N");
        var input = Directory.CreateDirectory(Path.Combine(root, "disc")).FullName;
        File.WriteAllText(Path.Combine(input, "original"), "disc");
        var request = new TranscodeJobRequest(input, Path.Combine(root, "output.mkv"), TranscodeVideoCodec.Hevc,
            TranscodeHardware.None, null, CopyVideo: true, ClientJobId: id, Bluray: Selection);
        var journal = Directory.CreateDirectory(Path.Combine(root, "bluray-jobs")).FullName;
        File.WriteAllText(Path.Combine(journal, id + ".json"), JsonSerializer.Serialize(new { Request = request, Playlist = Tracks, State = JobState.Running, Error = (string?)null }));
        using var engine = new FfmpegTranscodeEngine(new TranscodeEngineSettings { AppDataDir = root, MediaRoots = new Dictionary<string, string>() }, NullLogger<FfmpegTranscodeEngine>.Instance);
        await engine.StartAsync(default);
        try
        {
            var snapshot = engine.GetSnapshot(id);
            Assert.NotNull(snapshot);
            Assert.Equal("Failed", snapshot.State);
            Assert.Contains("restarted", snapshot.Error);
            Assert.True(File.Exists(Path.Combine(input, "original")));
            var retry = await engine.CreateAsync(request, default);
            Assert.Equal(id, retry.JobId);
            Assert.Equal("Failed", engine.GetSnapshot(id)!.State);
        }
        finally { await engine.StopAsync(default); }
    }

    [Fact]
    public void Output_cannot_be_inside_the_owned_disc_or_have_a_non_mkv_extension()
    {
        Assert.Throws<ArgumentException>(() => BlurayEndpoints.ValidateDestination(root, Path.Combine(root, "out.mkv")));
        Assert.Throws<ArgumentException>(() => BlurayEndpoints.ValidateDestination(root, root + "out.mp4"));
        BlurayEndpoints.ValidateDestination(root, root + ".mkv");
    }
}
