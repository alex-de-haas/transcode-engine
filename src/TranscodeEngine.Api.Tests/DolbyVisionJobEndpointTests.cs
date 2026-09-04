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

namespace TranscodeEngine.Api.Tests;

/// <summary>
/// <c>POST /jobs</c> with <c>dolbyVision</c>: the word is parsed, the conversion is refused wherever it could
/// not take effect — a re-encode, an extraction, a non-Matroska file at either end, a host without the tools
/// — and otherwise reaches the engine as the mode on the request. The engine's own refusal (an input that is
/// not profile 7) comes back as the same 400.
/// </summary>
public sealed class DolbyVisionJobEndpointTests
{
    private static readonly JobDescriptor Descriptor = new("job1", "/media/in.mkv", "/media/out.mkv", 120, 1000);

    private static async Task<(HttpClient Client, WebApplication App)> HostAsync(TranscodeEngineSettings settings, ITranscodeEngine engine)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton(engine);
        builder.Services.AddSingleton<TranscodeEventStream>();
        var app = builder.Build();
        app.MapTranscodeEndpoints();
        await app.StartAsync();
        return (app.GetTestClient(), app);
    }

    /// <summary>A media mount holding the named files, and settings whose tool paths either exist (three
    /// placeholder executables beside the media) or point nowhere.</summary>
    private static (TranscodeEngineSettings Settings, string Media) Fixture(bool tooling, params string[] files)
    {
        var root = Directory.CreateTempSubdirectory("te-dv").FullName;
        var media = Path.Combine(root, "media");
        Directory.CreateDirectory(media);
        foreach (var file in files)
        {
            File.WriteAllText(Path.Combine(media, file), "x");
        }

        string Tool(string name)
        {
            var path = Path.Combine(root, name);
            if (tooling)
            {
                File.WriteAllText(path, "#!/bin/sh\n");
            }

            return path;
        }

        var settings = new TranscodeEngineSettings
        {
            AppDataDir = root,
            MediaRoots = TranscodeEngineSettings.ParseMediaRoots($"media={media}", root),
            DoviToolPath = Tool("dovi_tool"),
            MkvmergePath = Tool("mkvmerge"),
            MkvextractPath = Tool("mkvextract"),
        };
        return (settings, media);
    }

    private static async Task<(HttpResponseMessage Response, TranscodeJobRequest? Seen)> PostAsync(TranscodeEngineSettings settings, object body)
    {
        TranscodeJobRequest? seen = null;
        var imposter = ITranscodeEngine.Imposter();
        imposter.CreateAsync(Arg<TranscodeJobRequest>.Any(), Arg<CancellationToken>.Any())
            .Returns((TranscodeJobRequest request, CancellationToken _) =>
            {
                seen = request;
                return Task.FromResult(Descriptor);
            });
        var (client, app) = await HostAsync(settings, imposter.Instance());
        await using var _ = app;

        var response = await client.PostAsJsonAsync("/jobs", body);
        await response.Content.LoadIntoBufferAsync();
        return (response, seen);
    }

    private static async Task<string> ErrorOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ErrorBody>())!.Error;

    [Fact]
    public async Task Post_UnknownDolbyVisionWord_ReturnsBadRequest()
    {
        var (settings, _) = Fixture(tooling: true, "in.mkv");
        var (response, seen) = await PostAsync(settings, new
        {
            inputMountLabel = "media", inputPath = "in.mkv", outputPath = "out.mkv", videoCodec = "copy", dolbyVision = "profile5",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("'profile5'", await ErrorOf(response));
        Assert.Null(seen);
    }

    [Fact]
    public async Task Post_ConversionWithAReencode_ReturnsBadRequest()
    {
        var (settings, _) = Fixture(tooling: true, "in.mkv");
        var (response, _) = await PostAsync(settings, new
        {
            inputMountLabel = "media", inputPath = "in.mkv", outputPath = "out.mkv", videoCodec = "hevc", dolbyVision = "toProfile81",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("copy", await ErrorOf(response));
    }

    [Fact]
    public async Task Post_ConversionToANonMatroskaOutput_ReturnsBadRequest()
    {
        var (settings, _) = Fixture(tooling: true, "in.mkv");
        var (response, _) = await PostAsync(settings, new
        {
            inputMountLabel = "media", inputPath = "in.mkv", outputPath = "out.mp4", videoCodec = "copy", dolbyVision = "toProfile81",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(".mkv", await ErrorOf(response));
    }

    [Fact]
    public async Task Post_ConversionOfANonMatroskaInput_ReturnsBadRequest()
    {
        var (settings, _) = Fixture(tooling: true, "in.mp4");
        var (response, _) = await PostAsync(settings, new
        {
            inputMountLabel = "media", inputPath = "in.mp4", outputPath = "out.mkv", videoCodec = "copy", dolbyVision = "toProfile81",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Matroska input", await ErrorOf(response));
    }

    [Fact]
    public async Task Post_ConversionWithoutTheTools_ReturnsBadRequest()
    {
        // An engine without the tools refuses rather than quietly copying: the caller asked for metadata the
        // output would not carry.
        var (settings, _) = Fixture(tooling: false, "in.mkv");
        var (response, seen) = await PostAsync(settings, new
        {
            inputMountLabel = "media", inputPath = "in.mkv", outputPath = "out.mkv", videoCodec = "copy", dolbyVision = "toProfile81",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("dovi_tool", await ErrorOf(response));
        Assert.Null(seen);
    }

    [Fact]
    public async Task Post_ConversionOnAnExtraction_ReturnsBadRequest()
    {
        var (settings, _) = Fixture(tooling: true, "in.mkv");
        var (response, _) = await PostAsync(settings, new
        {
            inputMountLabel = "media", inputPath = "in.mkv", dolbyVision = "toProfile81",
            outputs = new[] { new { path = "in.rus.mka", streamIndex = 1 } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("extraction", await ErrorOf(response));
    }

    [Fact]
    public async Task Post_ConversionOnACopyWithTheTools_HandsTheModeToTheEngine()
    {
        var (settings, _) = Fixture(tooling: true, "in.mkv");
        var (response, seen) = await PostAsync(settings, new
        {
            inputMountLabel = "media", inputPath = "in.mkv", outputPath = "out.mkv", videoCodec = "copy", dolbyVision = "toProfile81",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(seen);
        Assert.True(seen.CopyVideo);
        Assert.Equal(DolbyVisionMode.ToProfile81, seen.DolbyVision);
        Assert.True(seen.ConvertsDolbyVision);
    }

    [Fact]
    public async Task Post_ConversionOnAMergeThatNamesNoCodec_IsACopyAndIsAccepted()
    {
        // A merge copies the video by default, which is the copy the conversion needs.
        var (settings, _) = Fixture(tooling: true, "in.mkv", "in.rus.mka");
        var (response, seen) = await PostAsync(settings, new
        {
            inputMountLabel = "media", inputPath = "in.mkv", outputPath = "out.mkv", dolbyVision = "toProfile81",
            audioStreamIndexes = new[] { 1 },
            additionalInputs = new[] { new { path = "in.rus.mka", audioStreamIndexes = new[] { 0 } } },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(seen);
        Assert.True(seen.CopyVideo);
        Assert.Equal(DolbyVisionMode.ToProfile81, seen.DolbyVision);
        Assert.Single(seen.AdditionalInputs!);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("keep")]
    [InlineData("KEEP")]
    public async Task Post_KeepOrAbsent_IsTheDefaultAndNeedsNoTools(string? word)
    {
        var (settings, _) = Fixture(tooling: false, "in.mkv");
        var (response, seen) = await PostAsync(settings, new
        {
            inputMountLabel = "media", inputPath = "in.mkv", outputPath = "out.mkv", videoCodec = "copy", dolbyVision = word,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(seen);
        Assert.Equal(DolbyVisionMode.Keep, seen.DolbyVision);
    }

    [Fact]
    public async Task Post_EngineRefusal_IsABadRequestWithItsReason()
    {
        // Whether the input is profile 7 only the engine's probe can tell; its refusal reads like every other.
        var (settings, _) = Fixture(tooling: true, "in.mkv");
        var imposter = ITranscodeEngine.Imposter();
        imposter.CreateAsync(Arg<TranscodeJobRequest>.Any(), Arg<CancellationToken>.Any())
            .Returns((TranscodeJobRequest _, CancellationToken __) =>
                Task.FromException<JobDescriptor>(new ArgumentException("the input's video is Dolby Vision profile 8, not the dual-layer profile 7 this conversion rewrites.")));
        var (client, app) = await HostAsync(settings, imposter.Instance());
        await using var _ = app;

        var response = await client.PostAsJsonAsync("/jobs", new
        {
            inputMountLabel = "media", inputPath = "in.mkv", outputPath = "out.mkv", videoCodec = "copy", dolbyVision = "toProfile81",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("profile 8", await ErrorOf(response));
    }

    [Fact]
    public void HardwareStatus_CarriesTheTooling()
    {
        var status = new HardwareStatus(false, null, [], false, false, DateTimeOffset.UtcNow) with
        {
            Tools = new ToolingStatus(true, "2.3.3", "81.0"),
        };

        Assert.True(status.Tools!.DolbyVisionConversion);
        Assert.Equal("2.3.3", status.Tools.DoviTool);
        Assert.Equal("81.0", status.Tools.Mkvtoolnix);
    }

    private sealed record ErrorBody(string Error);
}
