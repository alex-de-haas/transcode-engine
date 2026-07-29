using System.Net;
using System.Net.Http.Json;
using Imposter.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using TranscodeEngine.Api.Api;
using TranscodeEngine.Api.Realtime;
using TranscodeEngine.Api.Transcoding;

[assembly: GenerateImposter(typeof(ITranscodeEngine))]

namespace TranscodeEngine.Api.Tests;

/// <summary>
/// Exercises <c>POST /jobs</c> against the real endpoint wiring (an in-memory TestServer hosting only
/// <see cref="TranscodeEndpoints.MapTranscodeEndpoints"/>) with a mocked <see cref="ITranscodeEngine"/>, so
/// no ffmpeg process ever starts. Covers the <c>mountLabel</c> selection and input validation.
/// </summary>
public sealed class TranscodeJobEndpointTests
{
    private static readonly JobDescriptor Descriptor = new("job1", "/media/in.mkv", "/media/out.mkv", 120, 1000);

    private static TranscodeEngineSettings Settings(string raw) =>
        new() { AppDataDir = "/tmp/te", MediaRoots = TranscodeEngineSettings.ParseMediaRoots(raw, "/tmp/te") };

    private static async Task<(HttpClient Client, WebApplication App)> HostAsync(TranscodeEngineSettings settings, ITranscodeEngine engine)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton(engine);
        // Registered so the unrelated GET /events handler's parameter resolves as a service (not an
        // inferred body) when the route table is built; this test never calls /events.
        builder.Services.AddSingleton<TranscodeEventStream>();
        var app = builder.Build();
        app.MapTranscodeEndpoints();
        await app.StartAsync();
        return (app.GetTestClient(), app);
    }

    [Fact]
    public async Task Post_UnknownMountLabel_ReturnsBadRequest()
    {
        var imposter = ITranscodeEngine.Imposter();
        var movies = Path.Combine(Path.GetTempPath(), "te-movies");
        var tv = Path.Combine(Path.GetTempPath(), "te-tv");
        var (client, app) = await HostAsync(Settings($"movies={movies},tv={tv}"), imposter.Instance());
        await using var _ = app;

        var response = await client.PostAsJsonAsync("/jobs", new
        {
            inputMountLabel = "nope",
            inputPath = "in.mkv",
            outputPath = "out.mkv",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ErrorBody>();
        Assert.Contains("nope", body!.Error);
    }

    [Fact]
    public async Task Post_MissingInput_ReturnsBadRequest()
    {
        var media = Directory.CreateTempSubdirectory("te-media").FullName;
        var imposter = ITranscodeEngine.Imposter();
        var (client, app) = await HostAsync(Settings($"media={media}"), imposter.Instance());
        await using var _ = app;

        var response = await client.PostAsJsonAsync("/jobs", new
        {
            inputMountLabel = "media",
            inputPath = "does-not-exist.mkv",
            outputPath = "out.mkv",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_ValidRequest_ReturnsDescriptor()
    {
        // The exact resolved paths are asserted in TranscodeEngineSettingsTests; here we only confirm a
        // known mountLabel + existing input is accepted (resolves, validates, hands off) and returns the
        // descriptor.
        var media = Directory.CreateTempSubdirectory("te-media").FullName;
        await File.WriteAllTextAsync(Path.Combine(media, "in.mkv"), "x");

        var imposter = ITranscodeEngine.Imposter();
        imposter.CreateAsync(Arg<TranscodeJobRequest>.Any(), Arg<CancellationToken>.Any())
            .Returns(Task.FromResult(Descriptor));

        var (client, app) = await HostAsync(Settings($"media={media}"), imposter.Instance());
        await using var _ = app;

        var response = await client.PostAsJsonAsync("/jobs", new
        {
            inputMountLabel = "media",
            inputPath = "in.mkv",
            outputPath = "out.mkv",
            videoCodec = "hevc",
            hardwareAcceleration = "none",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var descriptor = await response.Content.ReadFromJsonAsync<JobDescriptor>();
        Assert.Equal("job1", descriptor!.JobId);
    }

    [Fact]
    public async Task Post_CopyWithMaxHeight_ReturnsBadRequest()
    {
        var media = Directory.CreateTempSubdirectory("te-media").FullName;
        var (client, app) = await HostAsync(Settings($"media={media}"), ITranscodeEngine.Imposter().Instance());
        await using var _ = app;

        var response = await client.PostAsJsonAsync("/jobs", new
        {
            inputMountLabel = "media",
            inputPath = "in.mkv",
            outputPath = "out.mkv",
            videoCodec = "copy",
            maxHeight = 1080,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_DefaultAudioWithoutList_ReturnsBadRequest()
    {
        var media = Directory.CreateTempSubdirectory("te-media").FullName;
        var (client, app) = await HostAsync(Settings($"media={media}"), ITranscodeEngine.Imposter().Instance());
        await using var _ = app;

        var response = await client.PostAsJsonAsync("/jobs", new
        {
            inputMountLabel = "media",
            inputPath = "in.mkv",
            outputPath = "out.mkv",
            defaultAudioStreamIndex = 1,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_DefaultAudioNotInList_ReturnsBadRequest()
    {
        var media = Directory.CreateTempSubdirectory("te-media").FullName;
        var (client, app) = await HostAsync(Settings($"media={media}"), ITranscodeEngine.Imposter().Instance());
        await using var _ = app;

        var response = await client.PostAsJsonAsync("/jobs", new
        {
            inputMountLabel = "media",
            inputPath = "in.mkv",
            outputPath = "out.mkv",
            audioStreamIndexes = new[] { 1, 2 },
            defaultAudioStreamIndex = 9,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_SubtitleSelectionOnNonMatroska_ReturnsBadRequest()
    {
        var media = Directory.CreateTempSubdirectory("te-media").FullName;
        var (client, app) = await HostAsync(Settings($"media={media}"), ITranscodeEngine.Imposter().Instance());
        await using var _ = app;

        var response = await client.PostAsJsonAsync("/jobs", new
        {
            inputMountLabel = "media",
            inputPath = "in.mkv",
            outputPath = "out.mp4",
            subtitleStreamIndexes = new[] { 2 },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- merges: additional inputs and metadata overrides ----

    /// <summary>Creates a media root holding the named files, so path resolution and existence checks run
    /// against something real.</summary>
    private static string MediaWith(params string[] names)
    {
        var media = Directory.CreateTempSubdirectory("te-media").FullName;
        foreach (var name in names)
        {
            File.WriteAllText(Path.Combine(media, name), "x");
        }

        return media;
    }

    [Fact]
    public async Task Post_AdditionalInputSelectingNoStreams_ReturnsBadRequest()
    {
        var media = MediaWith("in.mkv", "dub.mka");
        var (client, app) = await HostAsync(Settings($"media={media}"), ITranscodeEngine.Imposter().Instance());
        await using var _ = app;

        var response = await client.PostAsJsonAsync("/jobs", new
        {
            inputMountLabel = "media",
            inputPath = "in.mkv",
            outputPath = "out.mkv",
            additionalInputs = new[] { new { path = "dub.mka" } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("selects no streams", (await response.Content.ReadFromJsonAsync<ErrorBody>())!.Error);
    }

    [Fact]
    public async Task Post_MissingAdditionalInput_ReturnsBadRequest()
    {
        var media = MediaWith("in.mkv");
        var (client, app) = await HostAsync(Settings($"media={media}"), ITranscodeEngine.Imposter().Instance());
        await using var _ = app;

        var response = await client.PostAsJsonAsync("/jobs", new
        {
            inputMountLabel = "media",
            inputPath = "in.mkv",
            outputPath = "out.mkv",
            additionalInputs = new[] { new { path = "gone.mka", audioStreamIndexes = new[] { 0 } } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("gone.mka", (await response.Content.ReadFromJsonAsync<ErrorBody>())!.Error);
    }

    [Fact]
    public async Task Post_MergeWithEncodeOnlyKnobAndNoCodec_ReturnsBadRequest()
    {
        // A merge with no videoCodec still defaults to a copy, so the encode-only options are as
        // contradictory here as they already are for an explicit videoCodec: "copy". Naming a real codec is
        // what buys them — see Post_MergeThatReEncodes_IsAcceptedWithItsEncodeOnlyKnobs.
        var media = MediaWith("in.mkv", "dub.mka");
        var (client, app) = await HostAsync(Settings($"media={media}"), ITranscodeEngine.Imposter().Instance());
        await using var _ = app;

        var response = await client.PostAsJsonAsync("/jobs", new
        {
            inputMountLabel = "media",
            inputPath = "in.mkv",
            outputPath = "out.mkv",
            maxHeight = 1080,
            additionalInputs = new[] { new { path = "dub.mka", audioStreamIndexes = new[] { 0 } } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("maxHeight", (await response.Content.ReadFromJsonAsync<ErrorBody>())!.Error);
    }

    [Fact]
    public async Task Post_OverrideOfAnUnmappedStream_ReturnsBadRequest()
    {
        // Without an explicit selection there is no output position to write the metadata to — the same
        // requirement a chosen default track carries.
        var media = MediaWith("in.mkv");
        var (client, app) = await HostAsync(Settings($"media={media}"), ITranscodeEngine.Imposter().Instance());
        await using var _ = app;

        var response = await client.PostAsJsonAsync("/jobs", new
        {
            inputMountLabel = "media",
            inputPath = "in.mkv",
            outputPath = "out.mkv",
            metadataOverrides = new[] { new { input = 0, streamIndex = 1, title = "Original" } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("maps explicitly", (await response.Content.ReadFromJsonAsync<ErrorBody>())!.Error);
    }

    [Fact]
    public async Task Post_OverrideNamingAnInputTheJobDoesNotHave_ReturnsBadRequest()
    {
        var media = MediaWith("in.mkv");
        var (client, app) = await HostAsync(Settings($"media={media}"), ITranscodeEngine.Imposter().Instance());
        await using var _ = app;

        var response = await client.PostAsJsonAsync("/jobs", new
        {
            inputMountLabel = "media",
            inputPath = "in.mkv",
            outputPath = "out.mkv",
            audioStreamIndexes = new[] { 1 },
            metadataOverrides = new[] { new { input = 3, streamIndex = 0, title = "Nope" } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("does not have", (await response.Content.ReadFromJsonAsync<ErrorBody>())!.Error);
    }

    [Fact]
    public async Task Post_EmptyOverride_ReturnsBadRequest()
    {
        var media = MediaWith("in.mkv");
        var (client, app) = await HostAsync(Settings($"media={media}"), ITranscodeEngine.Imposter().Instance());
        await using var _ = app;

        var response = await client.PostAsJsonAsync("/jobs", new
        {
            inputMountLabel = "media",
            inputPath = "in.mkv",
            outputPath = "out.mkv",
            audioStreamIndexes = new[] { 1 },
            metadataOverrides = new[] { new { input = 0, streamIndex = 1 } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("language or a title", (await response.Content.ReadFromJsonAsync<ErrorBody>())!.Error);
    }

    [Fact]
    public async Task Post_MergeWithNoCodec_IsAcceptedAndReachesTheEngineAsACopy()
    {
        // The default a merge keeps: re-encoding is the expensive, lossy direction, so omitting videoCodec
        // must never buy one — least of all for a caller written before merges could carry a codec at all.
        var media = MediaWith("in.mkv", "dub.mka");
        var imposter = ITranscodeEngine.Imposter();
        TranscodeJobRequest? seen = null;
        imposter.CreateAsync(Arg<TranscodeJobRequest>.Any(), Arg<CancellationToken>.Any())
            .Returns((TranscodeJobRequest request, CancellationToken _) =>
            {
                seen = request;
                return Task.FromResult(Descriptor);
            });
        var (client, app) = await HostAsync(Settings($"media={media}"), imposter.Instance());
        await using var _ = app;

        var response = await client.PostAsJsonAsync("/jobs", new
        {
            inputMountLabel = "media",
            inputPath = "in.mkv",
            outputPath = "out.mkv",
            audioStreamIndexes = new[] { 1 },
            additionalInputs = new[] { new { path = "dub.mka", audioStreamIndexes = new[] { 0 } } },
            metadataOverrides = new[] { new { input = 1, streamIndex = 0, language = "rus", title = "MVO wMedia" } },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(seen!.CopyVideo);
        var additional = Assert.Single(seen.AdditionalInputs!);
        Assert.Equal(Path.Combine(media, "dub.mka"), additional.Path);
        var over = Assert.Single(seen.MetadataOverrides!);
        Assert.Equal("MVO wMedia", over.Title);
    }

    [Fact]
    public async Task Post_MergeThatReEncodes_IsAcceptedWithItsEncodeOnlyKnobs()
    {
        // Appending tracks says nothing about what happens to the picture. Shrinking a remux while folding
        // its dubs in used to be two passes over the same gigabytes; naming a codec makes it one job, and the
        // encode-only knobs stop being contradictory the moment the video is actually being encoded.
        var media = MediaWith("in.mkv", "dub.mka");
        var imposter = ITranscodeEngine.Imposter();
        TranscodeJobRequest? seen = null;
        imposter.CreateAsync(Arg<TranscodeJobRequest>.Any(), Arg<CancellationToken>.Any())
            .Returns((TranscodeJobRequest request, CancellationToken _) =>
            {
                seen = request;
                return Task.FromResult(Descriptor);
            });
        var (client, app) = await HostAsync(Settings($"media={media}"), imposter.Instance());
        await using var _ = app;

        var response = await client.PostAsJsonAsync("/jobs", new
        {
            inputMountLabel = "media",
            inputPath = "in.mkv",
            outputPath = "out.mkv",
            videoCodec = "hevc",
            maxHeight = 1080,
            crf = 24,
            audioStreamIndexes = new[] { 1 },
            additionalInputs = new[] { new { path = "dub.mka", audioStreamIndexes = new[] { 0 } } },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(seen!.CopyVideo);
        Assert.Equal(TranscodeVideoCodec.Hevc, seen.VideoCodec);
        Assert.Equal(1080, seen.MaxHeight);
        Assert.Equal(24, seen.Crf);
        Assert.Equal(Path.Combine(media, "dub.mka"), Assert.Single(seen.AdditionalInputs!).Path);
    }

    [Fact]
    public async Task Post_MergeWithExplicitCopy_StillRefusesEncodeOnlyKnobs()
    {
        // The refusals moved from "is a merge" to "is a copy", so an explicit copy keeps them either way.
        var media = MediaWith("in.mkv", "dub.mka");
        var (client, app) = await HostAsync(Settings($"media={media}"), ITranscodeEngine.Imposter().Instance());
        await using var _ = app;

        var response = await client.PostAsJsonAsync("/jobs", new
        {
            inputMountLabel = "media",
            inputPath = "in.mkv",
            outputPath = "out.mkv",
            videoCodec = "copy",
            crf = 24,
            additionalInputs = new[] { new { path = "dub.mka", audioStreamIndexes = new[] { 0 } } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("crf", (await response.Content.ReadFromJsonAsync<ErrorBody>())!.Error);
    }

    [Fact]
    public async Task Post_OverrideWithOnlyEmptyValues_ReturnsBadRequest()
    {
        // Argument construction emits a field only when it has content, so accepting "" would report a
        // successful job that changes nothing.
        var media = MediaWith("in.mkv");
        var (client, app) = await HostAsync(Settings($"media={media}"), ITranscodeEngine.Imposter().Instance());
        await using var _ = app;

        var response = await client.PostAsJsonAsync("/jobs", new
        {
            inputMountLabel = "media",
            inputPath = "in.mkv",
            outputPath = "out.mkv",
            audioStreamIndexes = new[] { 1 },
            metadataOverrides = new[] { new { input = 0, streamIndex = 1, language = "", title = "  " } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("language or a title", (await response.Content.ReadFromJsonAsync<ErrorBody>())!.Error);
    }

    [Fact]
    public async Task Post_DuplicateOverridesForOneStream_ReturnsBadRequest()
    {
        // Both cannot be applied, and choosing one silently would discard the caller's other instruction.
        var media = MediaWith("in.mkv");
        var (client, app) = await HostAsync(Settings($"media={media}"), ITranscodeEngine.Imposter().Instance());
        await using var _ = app;

        var response = await client.PostAsJsonAsync("/jobs", new
        {
            inputMountLabel = "media",
            inputPath = "in.mkv",
            outputPath = "out.mkv",
            audioStreamIndexes = new[] { 1 },
            metadataOverrides = new[]
            {
                new { input = 0, streamIndex = 1, title = "First" },
                new { input = 0, streamIndex = 1, title = "Second" },
            },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("more than one metadata override", (await response.Content.ReadFromJsonAsync<ErrorBody>())!.Error);
    }

    [Fact]
    public async Task Post_OverrideOnAnAppendedTrackWithoutAnExplicitPrimaryList_ReturnsBadRequest()
    {
        // A null primary selection is one "copy every audio stream" mapping that expands to however many the
        // file holds, so an appended track after it has no position this app can compute — writing metadata
        // against the assumed one would relabel a primary track instead.
        var media = MediaWith("in.mkv", "dub.mka");
        var (client, app) = await HostAsync(Settings($"media={media}"), ITranscodeEngine.Imposter().Instance());
        await using var _ = app;

        var response = await client.PostAsJsonAsync("/jobs", new
        {
            inputMountLabel = "media",
            inputPath = "in.mkv",
            outputPath = "out.mkv",
            additionalInputs = new[] { new { path = "dub.mka", audioStreamIndexes = new[] { 0 } } },
            metadataOverrides = new[] { new { input = 1, streamIndex = 0, title = "Дубляж" } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("audioStreamIndexes must be listed explicitly", (await response.Content.ReadFromJsonAsync<ErrorBody>())!.Error);
    }

    private sealed record ErrorBody(string Error);
}
