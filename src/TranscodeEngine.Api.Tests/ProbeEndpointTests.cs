using System.Net;
using System.Net.Http.Json;
using Imposter.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using TranscodeEngine.Api.Api;
using TranscodeEngine.Api.Probing;
using TranscodeEngine.Api.Realtime;
using TranscodeEngine.Api.Transcoding;

[assembly: GenerateImposter(typeof(IMediaInspector))]

namespace TranscodeEngine.Api.Tests;

/// <summary>
/// Exercises <c>POST /probe</c> against the real endpoint wiring with a mocked inspector, so no ffprobe
/// process starts. Mount selection and the failure responses must match what job creation already returns
/// for the same mistakes.
/// </summary>
public sealed class ProbeEndpointTests
{
    private static readonly ProbeResponse Probe = new("mkv", 120.5, 8_000_000, 1_024,
        [new ProbedStreamInfo(0, ProbedStreamKind.Video, "hevc", "Main 10", "eng", null, true, false, 1920, 1080, 24, 10, HdrFormat.Hdr10, null, null)]);

    private static TranscodeEngineSettings Settings(string raw) =>
        new() { AppDataDir = "/tmp/te", MediaRoots = TranscodeEngineSettings.ParseMediaRoots(raw, "/tmp/te") };

    private static async Task<(HttpClient Client, WebApplication App)> HostAsync(TranscodeEngineSettings settings, IMediaInspector inspector)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton(inspector);
        builder.Services.AddSingleton(ITranscodeEngine.Imposter().Instance());
        builder.Services.AddSingleton<TranscodeEventStream>();
        var app = builder.Build();
        app.MapTranscodeEndpoints();
        await app.StartAsync();
        return (app.GetTestClient(), app);
    }

    [Fact]
    public async Task An_unknown_mount_label_is_rejected()
    {
        var imposter = IMediaInspector.Imposter();
        var (client, app) = await HostAsync(Settings("movies=/tmp/te-movies"), imposter.Instance());
        await using var _ = app;

        var response = await client.PostAsJsonAsync("/probe", new { mountLabel = "nope", path = "in.mkv" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ErrorBody>();
        Assert.Contains("nope", body!.Error);
    }

    [Fact]
    public async Task A_missing_file_is_rejected_before_ffprobe_runs()
    {
        var media = Directory.CreateTempSubdirectory("te-media").FullName;
        var imposter = IMediaInspector.Imposter();
        var (client, app) = await HostAsync(Settings($"media={media}"), imposter.Instance());
        await using var _ = app;

        var response = await client.PostAsJsonAsync("/probe", new { mountLabel = "media", path = "gone.mkv" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("gone.mkv", (await response.Content.ReadFromJsonAsync<ErrorBody>())!.Error);
    }

    [Fact]
    public async Task An_empty_path_is_rejected()
    {
        var imposter = IMediaInspector.Imposter();
        var (client, app) = await HostAsync(Settings("media=/tmp/te-media"), imposter.Instance());
        await using var _ = app;

        var response = await client.PostAsJsonAsync("/probe", new { path = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_path_outside_the_mount_is_rejected()
    {
        var media = Directory.CreateTempSubdirectory("te-media").FullName;
        var imposter = IMediaInspector.Imposter();
        var (client, app) = await HostAsync(Settings($"media={media}"), imposter.Instance());
        await using var _ = app;

        var response = await client.PostAsJsonAsync("/probe", new { mountLabel = "media", path = "../escape.mkv" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_readable_file_returns_the_normalized_description()
    {
        var media = Directory.CreateTempSubdirectory("te-media").FullName;
        await File.WriteAllTextAsync(Path.Combine(media, "in.mkv"), "x");
        var imposter = IMediaInspector.Imposter();
        imposter.InspectAsync(Arg<string>.Any(), Arg<CancellationToken>.Any())
            .Returns(Task.FromResult<ProbeResponse?>(Probe));
        var (client, app) = await HostAsync(Settings($"media={media}"), imposter.Instance());
        await using var _ = app;

        var response = await client.PostAsJsonAsync("/probe", new { mountLabel = "media", path = "in.mkv" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ProbeResponse>();
        Assert.Equal("mkv", body!.Container);
        var stream = Assert.Single(body.Streams);
        Assert.Equal(HdrFormat.Hdr10, stream.Hdr);
        Assert.Equal(ProbedStreamKind.Video, stream.Kind);
    }

    [Fact]
    public async Task A_file_that_is_not_media_is_a_bad_request_rather_than_an_empty_result()
    {
        var media = Directory.CreateTempSubdirectory("te-media").FullName;
        await File.WriteAllTextAsync(Path.Combine(media, "notes.mkv"), "not media");
        var imposter = IMediaInspector.Imposter();
        imposter.InspectAsync(Arg<string>.Any(), Arg<CancellationToken>.Any())
            .Returns(Task.FromResult<ProbeResponse?>(null));
        var (client, app) = await HostAsync(Settings($"media={media}"), imposter.Instance());
        await using var _ = app;

        var response = await client.PostAsJsonAsync("/probe", new { mountLabel = "media", path = "notes.mkv" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Enums_cross_the_wire_by_name()
    {
        // Asserted on the raw body on purpose. Reading the response back into ProbeResponse would round-trip
        // an ordinal happily and prove nothing: a consumer codes against the documented vocabulary, and
        // numbers would also make reordering a member a silent breaking change for anyone deployed.
        var media = Directory.CreateTempSubdirectory("te-media").FullName;
        await File.WriteAllTextAsync(Path.Combine(media, "in.mkv"), "x");
        var imposter = IMediaInspector.Imposter();
        imposter.InspectAsync(Arg<string>.Any(), Arg<CancellationToken>.Any())
            .Returns(Task.FromResult<ProbeResponse?>(Probe));
        var (client, app) = await HostAsync(Settings($"media={media}"), imposter.Instance());
        await using var _ = app;

        var response = await client.PostAsJsonAsync("/probe", new { mountLabel = "media", path = "in.mkv" });
        var raw = await response.Content.ReadAsStringAsync();

        Assert.Contains("\"kind\":\"Video\"", raw);
        Assert.Contains("\"hdr\":\"Hdr10\"", raw);
        Assert.DoesNotContain("\"kind\":0", raw);
    }

    private sealed record ErrorBody(string Error);
}
