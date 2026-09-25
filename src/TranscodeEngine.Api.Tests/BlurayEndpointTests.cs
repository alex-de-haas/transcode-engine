using System.Net;
using System.Net.Http.Json;
using Imposter.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using TranscodeEngine.Api.Api;
using TranscodeEngine.Api.Bluray;
using TranscodeEngine.Api.Transcoding;

namespace TranscodeEngine.Api.Tests;

public sealed class BlurayEndpointTests
{
    [Theory]
    [InlineData("disc", "movie.mkv", true)]
    [InlineData("../disc", "movie.mkv", false)]
    [InlineData("disc", "disc/movie.mkv", false)]
    [InlineData("disc", "../movie.mkv", false)]
    [InlineData("disc", "movie.mp4", false)]
    public async Task Job_resolves_mounted_paths_and_keeps_disc_outside_the_output(string input, string output, bool valid)
    {
        var root = Directory.CreateDirectory(Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "bluray-endpoint-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "disc/BDMV"));
            TranscodeJobRequest? submitted = null;
            var engine = ITranscodeEngine.Imposter();
            engine.CreateAsync(Arg<TranscodeJobRequest>.Any(), Arg<CancellationToken>.Any()).Returns((TranscodeJobRequest request, CancellationToken ct) =>
            {
                submitted = request;
                return Task.FromResult(new JobDescriptor(request.ClientJobId!, request.InputPath, request.OutputPath, 100, 200));
            });
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton(engine.Instance());
            builder.Services.AddSingleton(new TranscodeEngineSettings { AppDataDir = root, MediaRoots = new Dictionary<string, string> { ["movies"] = root } });
            builder.Services.AddSingleton<TranscodeEngine.Api.Realtime.TranscodeEventStream>();
            await using var app = builder.Build();
            app.MapTranscodeEndpoints();
            await app.StartAsync();
            var id = Guid.NewGuid();
            var selection = new BluraySelection("revision", "00001", 0, [new(1)], []);
            var response = await app.GetTestClient().PostAsJsonAsync("/jobs/bluray", new CreateBlurayRequest("movies", input, output, id, selection));
            Assert.Equal(valid ? HttpStatusCode.OK : HttpStatusCode.BadRequest, response.StatusCode);
            if (valid)
            {
                Assert.Equal(id.ToString("N"), submitted!.ClientJobId);
                Assert.Equal(Path.Combine(root, input), submitted.InputPath);
                Assert.Equal(Path.Combine(root, output), submitted.OutputPath);
                Assert.Equal(selection.PlaylistId, submitted.Bluray!.PlaylistId);
                Assert.True(submitted.CopyVideo);
            }
            else Assert.Null(submitted);
        }
        finally { Directory.Delete(root, true); }
    }
}
