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

    private sealed record ErrorBody(string Error);
}
