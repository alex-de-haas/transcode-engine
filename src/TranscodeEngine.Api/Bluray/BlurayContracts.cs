namespace TranscodeEngine.Api.Bluray;

/// <summary>A playlist-local MKVToolNix track id; these are not ffprobe stream indexes.</summary>
public sealed record BlurayTrack(int Id, string Type, string Codec, string? Language, string? Title,
    bool Default, bool Forced, string? PixelDimensions, int? Channels, string? DolbyVision);
/// <summary>A selectable disc playlist. Tracks are populated only for the explicitly inspected playlist.</summary>
public sealed record BlurayPlaylist(string Id, double DurationSeconds, int Chapters,
    IReadOnlyList<string> Clips, IReadOnlyList<BlurayTrack> Tracks, string? Error = null,
    IReadOnlyList<TranscodeEngine.Api.Probing.ProbedStreamInfo>? VideoFormats = null);
/// <summary>A revision-bound disc inventory. Revision changes invalidate previous selections.</summary>
public sealed record BlurayInspection(string Revision, long SizeBytes, IReadOnlyList<BlurayPlaylist> Playlists);
/// <summary>One selected audio/subtitle track and its explicit output metadata.</summary>
public sealed record BlurayTrackSelection(int Id, string? Language = null, string? Title = null,
    bool Default = false, bool Forced = false);
/// <summary>Disc selection persisted with a job, separate from ordinary file-stream selections.</summary>
public sealed record BluraySelection(string Revision, string PlaylistId, int VideoTrackId,
    IReadOnlyList<BlurayTrackSelection> Audio, IReadOnlyList<BlurayTrackSelection> Subtitles);
/// <summary>Disc inspection can be replaced in endpoint tests without launching a media tool.</summary>
public interface IBlurayInspector
{
    Task<BlurayInspection> InspectAsync(string root, string? playlistId, CancellationToken cancellationToken);
}
