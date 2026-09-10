using TranscodeEngine.Api.Transcoding;

namespace TranscodeEngine.Api.Api;

/// <summary>A mounted video file in the explicitly ordered join request.</summary>
public sealed record JoinInputRequest(string? MountLabel, string Path);

/// <summary>Joins two files; the stable client id makes retries safe after a lost response.</summary>
public sealed record JoinJobRequest(IReadOnlyList<JoinInputRequest> Inputs, string? OutputMountLabel, string OutputPath, Guid ClientJobId);

/// <summary>The joining contract is separate from sidecar merging and encoding.</summary>
public static class JoinEndpoints
{
    public static void Map(IEndpointRouteBuilder app) => app.MapPost("/jobs/join", async (
        JoinJobRequest request, ITranscodeEngine engine, TranscodeEngineSettings settings, CancellationToken ct) =>
    {
        try
        {
            if (request.Inputs is not { Count: 2 } || request.Inputs.Any(input => input is null || string.IsNullOrWhiteSpace(input.Path)))
                throw new ArgumentException("Choose exactly two video files in playback order.");
            if (request.ClientJobId == Guid.Empty)
                throw new ArgumentException("clientJobId is required for a join.");
            if (string.IsNullOrWhiteSpace(request.OutputPath) || !request.OutputPath.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("A join requires a Matroska (.mkv) output.");
            var inputs = request.Inputs.Select(input => settings.ResolveMediaPath(input.MountLabel, input.Path)).ToArray();
            var output = settings.ResolveMediaPath(request.OutputMountLabel ?? request.Inputs[0].MountLabel, request.OutputPath);
            var comparer = OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
            if (inputs.Distinct(comparer).Count() != 2 || inputs.Contains(output, comparer))
                throw new ArgumentException("Both inputs and the output must be different files.");
            if (inputs.Any(path => !File.Exists(path))) throw new ArgumentException("A selected video file is missing.");
            // Refuse symlinks in every existing path component, including parent directories.
            foreach (var path in inputs.Append(output))
                for (FileSystemInfo? entry = new FileInfo(path); entry is not null;
                     entry = entry is FileInfo file ? file.Directory : ((DirectoryInfo)entry).Parent)
                    if (entry.LinkTarget is not null) throw new ArgumentException("Joining through symbolic links is not supported.");
            var descriptor = await engine.CreateAsync(new TranscodeJobRequest(inputs[0], output,
                TranscodeVideoCodec.H264, TranscodeHardware.None, null, CopyVideo: true,
                JoinPaths: inputs, ClientJobId: request.ClientJobId.ToString("n")), ct);
            return Results.Ok(descriptor);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
    });
}
