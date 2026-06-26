namespace TranscodeEngine.Api.Transcoding;

/// <summary>
/// Engine configuration resolved once from the <c>HOSTY_*</c> / app environment Hosty injects. The app
/// never hard-codes ports or paths.
/// </summary>
public sealed class TranscodeEngineSettings
{
    /// <summary>App data dir (scratch / engine state live here).</summary>
    public required string AppDataDir { get; init; }

    /// <summary>Media roots keyed by their Hosty mount label. Both the input and the output of a job are
    /// resolved against the root the request selects by label (<see cref="ResolveMediaPath"/>), so a job
    /// reads and writes on the same filesystem as the consumer's catalog. Several roots come from a
    /// <c>multiple</c> <c>HOSTY_MOUNT_MEDIA</c> mount — one host path per catalog filesystem, the operator
    /// binding the same host paths (with the same labels) the consumer uses as catalog roots. A standalone
    /// run with no mount injected gets a single unlabeled fallback root at <c>{AppDataDir}/media</c>.</summary>
    public required IReadOnlyDictionary<string, string> MediaRoots { get; init; }

    /// <summary>Path to the <c>ffmpeg</c> binary; defaults to a PATH lookup.</summary>
    public string FfmpegPath { get; init; } = "ffmpeg";

    /// <summary>Path to the <c>ffprobe</c> binary; defaults to a PATH lookup.</summary>
    public string FfprobePath { get; init; } = "ffprobe";

    /// <summary>Default hardware acceleration when a job does not request one.</summary>
    public TranscodeHardware DefaultHardware { get; init; } = TranscodeHardware.Auto;

    /// <summary>VAAPI render node passed to ffmpeg's <c>-vaapi_device</c>. Passed through from the host via
    /// the manifest's <c>devices</c>.</summary>
    public string VaapiDevice { get; init; } = "/dev/dri/renderD128";

    /// <summary>How many jobs run at once. Hardware encoders have limited sessions, so this defaults to 1.</summary>
    public int MaxConcurrentJobs { get; init; } = 1;

    public static TranscodeEngineSettings FromConfiguration(IConfiguration configuration, string contentRoot)
    {
        string? Read(string key) => configuration[key] is { Length: > 0 } value ? value.Trim() : null;
        int ReadInt(string key, int fallback) => int.TryParse(Read(key), out var parsed) ? parsed : fallback;

        var appDataDir = Read("HOSTY_APP_DATA_DIR") ?? Path.Combine(contentRoot, "data");
        var mediaRoots = ParseMediaRoots(Read("HOSTY_MOUNT_MEDIA"), appDataDir);

        return new TranscodeEngineSettings
        {
            AppDataDir = appDataDir,
            MediaRoots = mediaRoots,
            FfmpegPath = Read("FFMPEG_PATH") ?? "ffmpeg",
            FfprobePath = Read("FFPROBE_PATH") ?? "ffprobe",
            DefaultHardware = ParseHardware(Read("HWACCEL")) ?? TranscodeHardware.Auto,
            VaapiDevice = Read("VAAPI_DEVICE") ?? "/dev/dri/renderD128",
            MaxConcurrentJobs = Math.Max(1, ReadInt("MAX_CONCURRENT_JOBS", 1)),
        };
    }

    /// <summary>Parses a hardware-acceleration setting value (case-insensitive); <c>null</c> when unset or
    /// unrecognized so the caller can apply its own default.</summary>
    internal static TranscodeHardware? ParseHardware(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "auto" => TranscodeHardware.Auto,
        "vaapi" => TranscodeHardware.Vaapi,
        "none" or "software" or "cpu" => TranscodeHardware.None,
        _ => null,
    };

    /// <summary>Parses <c>HOSTY_MOUNT_MEDIA</c> — a comma-joined list of <c>label=path</c> entries (Core
    /// injects container paths under docker) — into the label→root map. A host path may itself contain
    /// <c>'='</c>, so each entry is split on the <b>first</b> <c>'='</c> only; an entry with no <c>'='</c> or a
    /// blank explicit label falls back to the path's base name as its label. Matching is case-insensitive,
    /// and an entry that still has no usable label is skipped. With no mount injected (standalone/dev) a
    /// single unlabeled fallback root is used so the engine still runs.</summary>
    internal static IReadOnlyDictionary<string, string> ParseMediaRoots(string? raw, string appDataDir)
    {
        var roots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(raw))
        {
            foreach (var entry in raw.Split([',', '\n', '\r', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var separator = entry.IndexOf('=');
                var path = (separator >= 0 ? entry[(separator + 1)..] : entry).Trim();
                if (path.Length == 0)
                {
                    continue;
                }

                var label = separator >= 0 ? entry[..separator].Trim() : string.Empty;
                if (label.Length == 0)
                {
                    label = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                }

                if (label.Length == 0)
                {
                    continue;
                }

                roots[label] = Path.GetFullPath(path);
            }
        }

        if (roots.Count == 0)
        {
            roots[string.Empty] = Path.GetFullPath(Path.Combine(appDataDir, "media"));
        }

        return roots;
    }

    /// <summary>Resolves a request path against the media root selected by <paramref name="mountLabel"/> and
    /// guarantees the result stays inside it — a traversal like <c>../..</c> (or an absolute path outside the
    /// root) is rejected with <see cref="ArgumentException"/> so a job can never read or write off-mount.
    /// When the engine has a single root the label is optional; when it has several, an unknown or empty
    /// label is rejected so a job never touches the wrong filesystem.</summary>
    public string ResolveMediaPath(string? mountLabel, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ArgumentException("A path is required.", nameof(relativePath));
        }

        var root = Path.GetFullPath(ResolveRoot(mountLabel));
        var combined = Path.GetFullPath(Path.IsPathRooted(relativePath) ? relativePath : Path.Combine(root, relativePath));
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!string.Equals(combined, root, StringComparison.Ordinal) && !combined.StartsWith(rootPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException($"path '{relativePath}' resolves outside the media root.", nameof(relativePath));
        }

        return combined;
    }

    private string ResolveRoot(string? mountLabel)
    {
        var label = mountLabel?.Trim();
        if (!string.IsNullOrEmpty(label))
        {
            if (MediaRoots.TryGetValue(label, out var root))
            {
                return root;
            }

            throw new ArgumentException(
                $"No media mount labeled '{label}'. Bind the same host path into this app's 'media' mount with label '{label}' (configured: {FormatLabels()}).",
                nameof(mountLabel));
        }

        // No label is only unambiguous with a single root (one mount, or the standalone fallback).
        if (MediaRoots.Count == 1)
        {
            return MediaRoots.Values.First();
        }

        throw new ArgumentException(
            $"A mountLabel is required: the engine has multiple media mounts (configured: {FormatLabels()}).",
            nameof(mountLabel));
    }

    private string FormatLabels() =>
        string.Join(", ", MediaRoots.Keys.Select(key => key.Length == 0 ? "(default)" : key));
}
