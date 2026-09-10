using System.Net;
using System.Net.Http.Json;
using Imposter.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using TranscodeEngine.Api.Api;
using TranscodeEngine.Api.Transcoding;
using TranscodeEngine.Api.Realtime;

namespace TranscodeEngine.Api.Tests;

public sealed class JoinEndpointTests
{
    [Theory]
    [InlineData("b.mkv", "out.mkv", true)]
    [InlineData("a.mkv", "out.mkv", false)]
    [InlineData("b.mkv", "a.mkv", false)]
    [InlineData("b.mkv", "../out.mkv", false)]
    [InlineData("b.mkv", "out.mp4", false)]
    [InlineData("missing.mkv", "out.mkv", false)]
    public async Task Join_ValidatesAndResolvesBothMountedInputs(string second, string output, bool valid)
    {
        var directory = Directory.CreateTempSubdirectory("join-endpoint-");
        var root = directory.FullName;
        if (OperatingSystem.IsMacOS() && root.StartsWith("/var/")) root = "/private" + root;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "a.mkv"), "a");
            await File.WriteAllTextAsync(Path.Combine(root, "b.mkv"), "b");
            TranscodeJobRequest? submitted = null;
            var engine = ITranscodeEngine.Imposter();
            engine.CreateAsync(Arg<TranscodeJobRequest>.Any(), Arg<CancellationToken>.Any()).Returns((TranscodeJobRequest request, CancellationToken ct) =>
            {
                submitted = request;
                return Task.FromResult(new JobDescriptor(request.ClientJobId!, request.InputPath, request.OutputPath, 2, 2));
            });
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton(engine.Instance());
            builder.Services.AddSingleton(new TranscodeEngineSettings { AppDataDir = root, MediaRoots = new Dictionary<string, string> { ["movies"] = root } });
            builder.Services.AddSingleton<TranscodeEventStream>();
            await using var app = builder.Build();
            app.MapTranscodeEndpoints();
            await app.StartAsync();
            var id = Guid.NewGuid();
            var response = await app.GetTestClient().PostAsJsonAsync("/jobs/join", new JoinJobRequest(
                [new("movies", "a.mkv"), new("movies", second)], "movies", output, id));
            Assert.Equal(valid ? HttpStatusCode.OK : HttpStatusCode.BadRequest, response.StatusCode);
            if (valid)
            {
                Assert.Equal(new[] { Path.Combine(root, "a.mkv"), Path.Combine(root, second) }, submitted!.JoinPaths);
                Assert.Equal(id.ToString("n"), submitted.ClientJobId);
                Assert.True(submitted.CopyVideo);
                Assert.Null(submitted.AdditionalInputs);
            }
            else Assert.Null(submitted);
        }
        finally { directory.Delete(true); }
    }
}
