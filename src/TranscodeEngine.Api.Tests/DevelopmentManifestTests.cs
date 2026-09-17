using System.Text.Json;

namespace TranscodeEngine.Api.Tests;

public sealed class DevelopmentManifestTests
{
    private static JsonDocument Manifest() => JsonDocument.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "manifest.json")));

    [Fact]
    public void DevelopmentProfile_IsEditableAndNoninteractiveWithoutChangingDefaultRuntime()
    {
        using var document = Manifest();
        var manifest = document.RootElement;
        Assert.Equal("docker", manifest.GetProperty("defaultRuntime").GetString());
        var profiles = manifest.GetProperty("runtimeProfiles").EnumerateArray().ToArray();
        var dev = Assert.Single(profiles, profile => profile.GetProperty("key").GetString() == "dev");
        Assert.True(dev.GetProperty("development").GetBoolean());
        Assert.Equal("localCommand", dev.GetProperty("type").GetString());
        Assert.All(profiles.Where(profile => profile.GetProperty("key").GetString() != "dev"),
            profile => Assert.False(profile.TryGetProperty("development", out var value) && value.GetBoolean()));

        var runtimes = manifest.GetProperty("services")[0].GetProperty("runtimes");
        var runtime = runtimes.GetProperty("dev");
        Assert.Equal("source", runtime.GetProperty("artifact").GetString());
        Assert.Contains("dotnet restore", runtime.GetProperty("setup").GetString());
        var command = runtime.GetProperty("command").GetString()!;
        Assert.StartsWith("dotnet watch ", command);
        Assert.Contains("--non-interactive", command);
        Assert.Contains("--no-launch-profile", command);
        Assert.DoesNotContain("--no-restore", command); // New package references must restore on source reload.
        Assert.Equal("1", runtime.GetProperty("environment").GetProperty("DOTNET_WATCH_RESTART_ON_RUDE_EDIT").GetString());
        Assert.DoesNotContain("watch", runtimes.GetProperty("local").GetProperty("command").GetString());
        Assert.False(runtimes.GetProperty("docker").TryGetProperty("devices", out _));
    }

    [Fact]
    public void DevelopmentProfile_KeepsCoreAssignedPortsDataAndRequiredMediaMount()
    {
        using var document = Manifest();
        var manifest = document.RootElement;
        var runtime = manifest.GetProperty("services")[0].GetProperty("runtimes").GetProperty("dev");
        var port = Assert.Single(runtime.GetProperty("ports").EnumerateArray());
        Assert.Equal("control", port.GetProperty("key").GetString());
        Assert.False(port.TryGetProperty("localPort", out _));
        Assert.False(port.TryGetProperty("hostPort", out _));
        Assert.False(runtime.GetProperty("environment").TryGetProperty("HOSTY_PORT_CONTROL", out _));
        var target = Assert.Single(manifest.GetProperty("data").GetProperty("targets").EnumerateArray(),
            item => item.GetProperty("runtime").GetString() == "dev");
        Assert.Equal("HOSTY_APP_DATA_DIR", target.GetProperty("environment").GetString());
        Assert.False(target.TryGetProperty("containerPath", out _));
        var media = manifest.GetProperty("externalMounts").GetProperty("media");
        Assert.True(media.GetProperty("required").GetBoolean());
        Assert.Equal("engine", media.GetProperty("service").GetString());
    }
}
