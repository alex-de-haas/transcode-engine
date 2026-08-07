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
/// <c>POST /jobs</c> with an <c>outputs</c> list — the extraction shape. Everything a composed output needs
/// is refused rather than ignored, for the reason the encode-only knobs are refused on a video copy:
/// accepting a field that cannot take effect reports success for a job that does something else.
/// Runs against the real endpoint wiring with a mocked engine, so no ffmpeg process ever starts.
/// </summary>
public sealed class ExtractJobEndpointTests
{
    private static readonly JobDescriptor Descriptor =
        new("job1", "/media/in.mkv", null, 120, 1000, ["/media/in.rus.mka"]);

    private static TranscodeEngineSettings Settings(string raw) =>
        new() { AppDataDir = "/tmp/te", MediaRoots = TranscodeEngineSettings.ParseMediaRoots(raw, "/tmp/te") };

    private static string MediaWith(params string[] names)
    {
        var media = Directory.CreateTempSubdirectory("te-extract").FullName;
        foreach (var name in names)
        {
            File.WriteAllText(Path.Combine(media, name), "x");
        }

        return media;
    }

    private static async Task<(HttpClient Client, WebApplication App)> HostAsync(
        TranscodeEngineSettings settings, ITranscodeEngine engine)
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

    /// <summary>Posts an extraction and hands back the response plus whatever reached the engine.</summary>
    private static async Task<(HttpResponseMessage Response, TranscodeJobRequest? Seen)> PostAsync(object body)
    {
        var media = MediaWith("in.mkv");
        TranscodeJobRequest? seen = null;
        var imposter = ITranscodeEngine.Imposter();
        imposter.CreateAsync(Arg<TranscodeJobRequest>.Any(), Arg<CancellationToken>.Any())
            .Returns((TranscodeJobRequest request, CancellationToken _) =>
            {
                seen = request;
                return Task.FromResult(Descriptor);
            });
        var (client, app) = await HostAsync(Settings($"media={media}"), imposter.Instance());
        await using var _ = app;

        var response = await client.PostAsJsonAsync("/jobs", body);
        // Read the body here: the response outlives the host, but its content stream does not.
        await response.Content.LoadIntoBufferAsync();
        return (response, seen);
    }

    private static async Task<string> ErrorOf(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ErrorBody>())!.Error;
    }

    [Fact]
    public async Task Post_Extraction_ReachesTheEngineWithResolvedPaths()
    {
        var (response, seen) = await PostAsync(new
        {
            inputMountLabel = "media",
            inputPath = "in.mkv",
            outputs = new object[]
            {
                new { path = "in.rus.mka", streamIndex = 1, language = "rus", title = "AniDUB" },
                // No language or title at all — the fields a caller omits entirely.
                new { path = "in.rus.srt", streamIndex = 3 },
            },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(seen!.IsExtraction);
        Assert.Null(seen.OutputPath);
        Assert.Equal(2, seen.Outputs!.Count);

        var dub = seen.Outputs[0];
        Assert.EndsWith("in.rus.mka", dub.Path);
        Assert.True(Path.IsPathRooted(dub.Path));
        Assert.Equal(1, dub.StreamIndex);
        Assert.Equal(ExtractionCodec.Copy, dub.Codec);
        Assert.Equal("rus", dub.Language);
        Assert.Equal("AniDUB", dub.Title);

        // A field the caller left out stays null, so the source stream's own tag survives.
        Assert.Null(seen.Outputs[1].Language);
        Assert.Null(seen.Outputs[1].Title);
    }

    [Fact]
    public async Task Post_ExtractionWithWhitespaceMetadata_SendsNothing()
    {
        // Argument construction emits a field only when it has content, so carrying "  " through would claim
        // a metadata write the output never gets.
        var (_, seen) = await PostAsync(new
        {
            inputMountLabel = "media",
            inputPath = "in.mkv",
            outputs = new[] { new { path = "in.rus.mka", streamIndex = 1, language = "  ", title = " " } },
        });

        Assert.Null(seen!.Outputs![0].Language);
        Assert.Null(seen.Outputs[0].Title);
    }

    [Theory]
    [InlineData("copy", ExtractionCodec.Copy)]
    [InlineData("srt", ExtractionCodec.Srt)]
    [InlineData("subrip", ExtractionCodec.Srt)]
    [InlineData("ass", ExtractionCodec.Ass)]
    [InlineData("webvtt", ExtractionCodec.WebVtt)]
    [InlineData("vtt", ExtractionCodec.WebVtt)]
    public async Task Post_ExtractionCodec_IsNormalized(string raw, ExtractionCodec expected)
    {
        var (response, seen) = await PostAsync(new
        {
            inputMountLabel = "media",
            inputPath = "in.mkv",
            outputs = new[] { new { path = "in.rus.srt", streamIndex = 3, codec = raw } },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, seen!.Outputs![0].Codec);
    }

    [Fact]
    public async Task Post_UnsupportedExtractionCodec_ReturnsBadRequest()
    {
        // eac3 belongs to a composed output's audioTargets. Re-encoding on the way out would make this a
        // second, worse encoder surface.
        var (response, _) = await PostAsync(new
        {
            inputMountLabel = "media",
            inputPath = "in.mkv",
            outputs = new[] { new { path = "in.rus.mka", streamIndex = 1, codec = "eac3" } },
        });

        Assert.Contains("eac3", await ErrorOf(response));
    }

    [Fact]
    public async Task Post_OutputPathAndOutputs_ReturnsBadRequest()
    {
        var (response, _) = await PostAsync(new
        {
            inputMountLabel = "media",
            inputPath = "in.mkv",
            outputPath = "out.mkv",
            outputs = new[] { new { path = "in.rus.mka", streamIndex = 1 } },
        });

        Assert.Contains("mutually exclusive", await ErrorOf(response));
    }

    [Theory]
    [InlineData("videoCodec", "hevc")]
    [InlineData("qualityLevel", "small")]
    public async Task Post_EncodeOnlyKnobOnAnExtraction_ReturnsBadRequest(string field, string value)
    {
        var (response, _) = await PostAsync(new Dictionary<string, object>
        {
            ["inputMountLabel"] = "media",
            ["inputPath"] = "in.mkv",
            ["outputs"] = new[] { new { path = "in.rus.mka", streamIndex = 1 } },
            [field] = value,
        });

        Assert.Contains(field, await ErrorOf(response));
    }

    [Fact]
    public async Task Post_MaxHeightOnAnExtraction_ReturnsBadRequest()
    {
        var (response, _) = await PostAsync(new
        {
            inputMountLabel = "media",
            inputPath = "in.mkv",
            maxHeight = 1080,
            outputs = new[] { new { path = "in.rus.mka", streamIndex = 1 } },
        });

        Assert.Contains("maxHeight", await ErrorOf(response));
    }

    [Fact]
    public async Task Post_ExplicitAcceleratorOnAnExtraction_ReturnsBadRequest()
    {
        var (response, _) = await PostAsync(new
        {
            inputMountLabel = "media",
            inputPath = "in.mkv",
            hardwareAcceleration = "vaapi",
            outputs = new[] { new { path = "in.rus.mka", streamIndex = 1 } },
        });

        Assert.Contains("hardwareAcceleration", await ErrorOf(response));
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("")]
    public async Task Post_AutoAcceleratorOnAnExtraction_IsAcceptedAndInert(string hardware)
    {
        // 'auto' is what a client sends by default; failing the ordinary call over a field that means nothing
        // here would be gratuitous.
        var (response, seen) = await PostAsync(new
        {
            inputMountLabel = "media",
            inputPath = "in.mkv",
            hardwareAcceleration = hardware,
            outputs = new[] { new { path = "in.rus.mka", streamIndex = 1 } },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(TranscodeHardware.None, seen!.HardwareAcceleration);
    }

    [Fact]
    public async Task Post_AdditionalInputsOnAnExtraction_ReturnsBadRequest()
    {
        var (response, _) = await PostAsync(new
        {
            inputMountLabel = "media",
            inputPath = "in.mkv",
            additionalInputs = new[] { new { path = "dub.mka", audioStreamIndexes = new[] { 0 } } },
            outputs = new[] { new { path = "in.rus.mka", streamIndex = 1 } },
        });

        Assert.Contains("additionalInputs", await ErrorOf(response));
    }

    [Fact]
    public async Task Post_TrackSelectionOnAnExtraction_ReturnsBadRequest()
    {
        var (response, _) = await PostAsync(new
        {
            inputMountLabel = "media",
            inputPath = "in.mkv",
            audioStreamIndexes = new[] { 1 },
            outputs = new[] { new { path = "in.rus.mka", streamIndex = 1 } },
        });

        Assert.Contains("audioStreamIndexes", await ErrorOf(response));
    }

    [Fact]
    public async Task Post_DefaultTrackOnAnExtraction_ReturnsBadRequest()
    {
        var (response, _) = await PostAsync(new
        {
            inputMountLabel = "media",
            inputPath = "in.mkv",
            defaultAudioStreamIndex = 1,
            outputs = new[] { new { path = "in.rus.mka", streamIndex = 1 } },
        });

        Assert.Contains("default track", await ErrorOf(response));
    }

    [Fact]
    public async Task Post_AudioTargetsOnAnExtraction_ReturnsBadRequest()
    {
        var (response, _) = await PostAsync(new
        {
            inputMountLabel = "media",
            inputPath = "in.mkv",
            audioTargets = new[] { new { input = 0, streamIndex = 1, codec = "eac3" } },
            outputs = new[] { new { path = "in.rus.mka", streamIndex = 1 } },
        });

        Assert.Contains("audioTargets", await ErrorOf(response));
    }

    [Fact]
    public async Task Post_MetadataOverridesOnAnExtraction_ReturnsBadRequest()
    {
        // Language and title belong on the output itself; an override's (input, streamIndex) pair exists to
        // locate a position in a composed output, and an extracted stream has none.
        var (response, _) = await PostAsync(new
        {
            inputMountLabel = "media",
            inputPath = "in.mkv",
            metadataOverrides = new[] { new { input = 0, streamIndex = 1, language = "rus" } },
            outputs = new[] { new { path = "in.rus.mka", streamIndex = 1 } },
        });

        Assert.Contains("metadataOverrides", await ErrorOf(response));
    }

    [Fact]
    public async Task Post_TwoOutputsAtOnePath_ReturnsBadRequest()
    {
        // They would race each other's publish and leave whichever finished last, losing a track silently.
        var (response, _) = await PostAsync(new
        {
            inputMountLabel = "media",
            inputPath = "in.mkv",
            outputs = new[]
            {
                new { path = "in.rus.mka", streamIndex = 1 },
                new { path = "in.rus.mka", streamIndex = 2 },
            },
        });

        Assert.Contains("two outputs write to", await ErrorOf(response));
    }

    [Fact]
    public async Task Post_OutputEqualToTheInput_ReturnsBadRequest()
    {
        var (response, _) = await PostAsync(new
        {
            inputMountLabel = "media",
            inputPath = "in.mkv",
            outputs = new[] { new { path = "in.mkv", streamIndex = 1 } },
        });

        Assert.Contains("differ from inputPath", await ErrorOf(response));
    }

    [Fact]
    public async Task Post_OutputThatOnlyDiffersFromTheInputByCase_IsRefusedOffLinux()
    {
        // On a case-insensitive volume these name one file, and publishing would move the extracted track
        // over the film. Ordinal comparison is kept on Linux, where the spelling really is a different file.
        var (response, _) = await PostAsync(new
        {
            inputMountLabel = "media",
            inputPath = "in.mkv",
            outputs = new[] { new { path = "IN.MKV", streamIndex = 1 } },
        });

        if (OperatingSystem.IsLinux())
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return;
        }

        Assert.Contains("differ from inputPath", await ErrorOf(response));
    }

    [Fact]
    public async Task Post_TwoOutputsDifferingOnlyByCase_AreRefusedOffLinux()
    {
        var (response, _) = await PostAsync(new
        {
            inputMountLabel = "media",
            inputPath = "in.mkv",
            outputs = new[]
            {
                new { path = "in.rus.mka", streamIndex = 1 },
                new { path = "IN.RUS.MKA", streamIndex = 2 },
            },
        });

        if (OperatingSystem.IsLinux())
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return;
        }

        Assert.Contains("two outputs write to", await ErrorOf(response));
    }

    [Fact]
    public async Task Post_NullOutputEntry_ReturnsBadRequest()
    {
        // A JSON array can hold a literal null whatever the element type says; reading through it would be a
        // 500 for an ordinary malformed request.
        var media = MediaWith("in.mkv");
        var (client, app) = await HostAsync(Settings($"media={media}"), ITranscodeEngine.Imposter().Instance());
        await using var _ = app;

        var response = await client.PostAsync(
            "/jobs",
            new StringContent(
                """{"inputMountLabel":"media","inputPath":"in.mkv","outputs":[null]}""",
                System.Text.Encoding.UTF8,
                "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("must be an object with a path", (await response.Content.ReadFromJsonAsync<ErrorBody>())!.Error);
    }

    [Fact]
    public async Task Post_OutputMountLabel_IsTheDefaultForEveryOutput()
    {
        // It means the same thing here as on a composed job — where the results are written — so setting it
        // and having every output land on the input's mount instead would ignore a field the caller set.
        var media = MediaWith("in.mkv");
        var elsewhere = Directory.CreateTempSubdirectory("te-extract-out").FullName;
        TranscodeJobRequest? seen = null;
        var imposter = ITranscodeEngine.Imposter();
        imposter.CreateAsync(Arg<TranscodeJobRequest>.Any(), Arg<CancellationToken>.Any())
            .Returns((TranscodeJobRequest request, CancellationToken _) =>
            {
                seen = request;
                return Task.FromResult(Descriptor);
            });
        var (client, app) = await HostAsync(Settings($"media={media},archive={elsewhere}"), imposter.Instance());
        await using var _ = app;

        var response = await client.PostAsJsonAsync("/jobs", new
        {
            inputMountLabel = "media",
            inputPath = "in.mkv",
            outputMountLabel = "archive",
            outputs = new object[]
            {
                new { path = "in.rus.mka", streamIndex = 1 },
                // An entry naming its own mount still wins.
                new { mountLabel = "media", path = "in.eng.srt", streamIndex = 3 },
            },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith(elsewhere, seen!.Outputs![0].Path);
        Assert.StartsWith(media, seen.Outputs[1].Path);
    }

    [Fact]
    public async Task Post_NegativeStreamIndex_ReturnsBadRequest()
    {
        var (response, _) = await PostAsync(new
        {
            inputMountLabel = "media",
            inputPath = "in.mkv",
            outputs = new[] { new { path = "in.rus.mka", streamIndex = -1 } },
        });

        Assert.Contains("non-negative", await ErrorOf(response));
    }

    [Fact]
    public async Task Post_OutputWithoutAPath_ReturnsBadRequest()
    {
        var (response, _) = await PostAsync(new
        {
            inputMountLabel = "media",
            inputPath = "in.mkv",
            outputs = new[] { new { path = "", streamIndex = 1 } },
        });

        Assert.Contains("path", await ErrorOf(response));
    }

    [Fact]
    public async Task Post_MissingInput_ReturnsBadRequest()
    {
        var media = Directory.CreateTempSubdirectory("te-extract").FullName;
        var (client, app) = await HostAsync(Settings($"media={media}"), ITranscodeEngine.Imposter().Instance());
        await using var _ = app;

        var response = await client.PostAsJsonAsync("/jobs", new
        {
            inputMountLabel = "media",
            inputPath = "does-not-exist.mkv",
            outputs = new[] { new { path = "in.rus.mka", streamIndex = 1 } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_EngineRefusal_BecomesTheSameBadRequest()
    {
        // The stream-kind check lives in the engine (it needs the input's streams), but a caller must not be
        // able to tell which side of the line a rule sat on.
        var media = MediaWith("in.mkv");
        var imposter = ITranscodeEngine.Imposter();
        imposter.CreateAsync(Arg<TranscodeJobRequest>.Any(), Arg<CancellationToken>.Any())
            .Returns((TranscodeJobRequest _, CancellationToken __) =>
                Task.FromException<JobDescriptor>(new ArgumentException("the input has no stream 9.")));
        var (client, app) = await HostAsync(Settings($"media={media}"), imposter.Instance());
        await using var _ = app;

        var response = await client.PostAsJsonAsync("/jobs", new
        {
            inputMountLabel = "media",
            inputPath = "in.mkv",
            outputs = new[] { new { path = "in.rus.mka", streamIndex = 9 } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("no stream 9", (await response.Content.ReadFromJsonAsync<ErrorBody>())!.Error);
    }

    private sealed record ErrorBody(string Error);
}
