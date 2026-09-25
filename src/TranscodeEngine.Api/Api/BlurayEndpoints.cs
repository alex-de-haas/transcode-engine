using TranscodeEngine.Api.Bluray;
using TranscodeEngine.Api.Transcoding;

namespace TranscodeEngine.Api.Api;

/// <summary>A mounted disc, optionally narrowed to a playlist for track inspection.</summary>
public sealed record InspectBlurayRequest(string? MountLabel, string Path, string? PlaylistId = null);
/// <summary>An explicit, retryable playlist-to-MKV operation.</summary>
public sealed record CreateBlurayRequest(string? MountLabel, string Path, string OutputPath,
    Guid ClientJobId, BluraySelection Selection);

/// <summary>Disc operations share the mounted-path boundary and job infrastructure with ordinary conversion.</summary>
public static class BlurayEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/bluray/inspect", async (InspectBlurayRequest request, [Microsoft.AspNetCore.Mvc.FromServices] IBlurayInspector inspector,
            TranscodeEngineSettings settings, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await inspector.InspectAsync(settings.ResolveMediaPath(request.MountLabel, request.Path), request.PlaylistId, ct));
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException)
            { return Results.BadRequest(new { error = ex.Message }); }
        });
        app.MapPost("/jobs/bluray", async (CreateBlurayRequest request, ITranscodeEngine engine,
            TranscodeEngineSettings settings, CancellationToken ct) =>
        {
            try
            {
                if (request.ClientJobId == Guid.Empty || request.Selection is null)
                    throw new ArgumentException("clientJobId and selection are required.");
                var root = settings.ResolveMediaPath(request.MountLabel, request.Path);
                var output = settings.ResolveMediaPath(request.MountLabel, request.OutputPath);
                ValidateDestination(root, output);
                return Results.Ok(await engine.CreateAsync(new(root, output, TranscodeVideoCodec.Hevc,
                    TranscodeHardware.None, null, CopyVideo: true, ClientJobId: request.ClientJobId.ToString("N"),
                    Bluray: request.Selection), ct));
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException)
            { return Results.BadRequest(new { error = ex.Message }); }
        });
    }

    internal static void ValidateDestination(string root, string output)
    {
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (!output.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase) ||
            output.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, comparison))
            throw new ArgumentException("Choose an MKV destination outside the disc directory.");
        for (FileSystemInfo? entry = new FileInfo(output); entry is not null;
             entry = entry is FileInfo f ? f.Directory : ((DirectoryInfo)entry).Parent)
            if (entry.LinkTarget is not null) throw new ArgumentException("The output path cannot contain symbolic links.");
    }
}
