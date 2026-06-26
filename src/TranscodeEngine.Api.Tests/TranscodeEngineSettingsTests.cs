using TranscodeEngine.Api.Transcoding;

namespace TranscodeEngine.Api.Tests;

public sealed class TranscodeEngineSettingsTests
{
    private static string Full(params string[] segments) => Path.GetFullPath(Path.Combine(segments));

    private static TranscodeEngineSettings WithRoots(string? raw, string appDataDir = "/tmp/transcode-engine") =>
        new() { AppDataDir = appDataDir, MediaRoots = TranscodeEngineSettings.ParseMediaRoots(raw, appDataDir) };

    // ---- ParseMediaRoots ----

    [Fact]
    public void ParseMediaRoots_NoMount_FallsBackToSingleUnlabeledRoot()
    {
        var appDataDir = Path.Combine(Path.GetTempPath(), "te-app");

        var roots = TranscodeEngineSettings.ParseMediaRoots(null, appDataDir);

        var entry = Assert.Single(roots);
        Assert.Equal(string.Empty, entry.Key);
        Assert.Equal(Full(appDataDir, "media"), entry.Value);
    }

    [Fact]
    public void ParseMediaRoots_ParsesCommaJoinedLabelPathEntries()
    {
        var movies = Path.Combine(Path.GetTempPath(), "media", "movies");
        var tv = Path.Combine(Path.GetTempPath(), "media", "tv");

        var roots = TranscodeEngineSettings.ParseMediaRoots($"movies={movies},tv={tv}", "/tmp/te");

        Assert.Equal(2, roots.Count);
        Assert.Equal(Path.GetFullPath(movies), roots["movies"]);
        Assert.Equal(Path.GetFullPath(tv), roots["tv"]);
    }

    [Fact]
    public void ParseMediaRoots_SplitsOnFirstEquals_WhenPathContainsEquals()
    {
        var roots = TranscodeEngineSettings.ParseMediaRoots("movies=/srv/a=x", "/tmp/te");

        var entry = Assert.Single(roots);
        Assert.Equal("movies", entry.Key);
        Assert.Equal(Path.GetFullPath("/srv/a=x"), entry.Value);
    }

    [Fact]
    public void ParseMediaRoots_NoLabel_FallsBackToBaseName()
    {
        var roots = TranscodeEngineSettings.ParseMediaRoots("/srv/media/movies", "/tmp/te");

        var entry = Assert.Single(roots);
        Assert.Equal("movies", entry.Key);
        Assert.Equal(Path.GetFullPath("/srv/media/movies"), entry.Value);
    }

    [Fact]
    public void ParseMediaRoots_BlankExplicitLabel_FallsBackToBaseName()
    {
        var roots = TranscodeEngineSettings.ParseMediaRoots("=/srv/media/anime", "/tmp/te");

        var entry = Assert.Single(roots);
        Assert.Equal("anime", entry.Key);
    }

    // ---- ParseHardware ----

    [Theory]
    [InlineData("auto", TranscodeHardware.Auto)]
    [InlineData("VAAPI", TranscodeHardware.Vaapi)]
    [InlineData("videotoolbox", TranscodeHardware.VideoToolbox)]
    [InlineData("AMF", TranscodeHardware.Amf)]
    [InlineData("none", TranscodeHardware.None)]
    [InlineData("software", TranscodeHardware.None)]
    public void ParseHardware_RecognizedValues(string raw, TranscodeHardware expected) =>
        Assert.Equal(expected, TranscodeEngineSettings.ParseHardware(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nvenc")]
    public void ParseHardware_UnknownOrEmpty_ReturnsNull(string? raw) =>
        Assert.Null(TranscodeEngineSettings.ParseHardware(raw));

    // ---- ResolveMediaPath ----

    [Fact]
    public void ResolveMediaPath_SingleRoot_NullLabel_ResolvesUnderIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "media", "only");
        var settings = WithRoots($"only={root}");

        var resolved = settings.ResolveMediaPath(mountLabel: null, relativePath: "Movies/in.mkv");

        Assert.Equal(Full(root, "Movies", "in.mkv"), resolved);
    }

    [Fact]
    public void ResolveMediaPath_PicksRootByLabel()
    {
        var movies = Path.Combine(Path.GetTempPath(), "media", "movies");
        var tv = Path.Combine(Path.GetTempPath(), "media", "tv");
        var settings = WithRoots($"movies={movies},tv={tv}");

        var resolved = settings.ResolveMediaPath("tv", "Show/ep.mkv");

        Assert.Equal(Full(tv, "Show", "ep.mkv"), resolved);
    }

    [Fact]
    public void ResolveMediaPath_MatchesLabelCaseInsensitively()
    {
        var movies = Path.Combine(Path.GetTempPath(), "media", "movies");
        var tv = Path.Combine(Path.GetTempPath(), "media", "tv");
        var settings = WithRoots($"movies={movies},tv={tv}");

        var resolved = settings.ResolveMediaPath("MOVIES", "in.mkv");

        Assert.Equal(Full(movies, "in.mkv"), resolved);
    }

    [Fact]
    public void ResolveMediaPath_BlankPath_Throws()
    {
        var settings = WithRoots($"movies={Path.Combine(Path.GetTempPath(), "media", "movies")}");

        Assert.Throws<ArgumentException>(() => settings.ResolveMediaPath("movies", relativePath: " "));
    }

    [Fact]
    public void ResolveMediaPath_UnknownLabel_Throws()
    {
        var settings = WithRoots($"movies={Path.Combine(Path.GetTempPath(), "media", "movies")}");

        var error = Assert.Throws<ArgumentException>(() => settings.ResolveMediaPath("tv", "in.mkv"));
        Assert.Contains("tv", error.Message);
    }

    [Fact]
    public void ResolveMediaPath_MultipleRoots_NoLabel_Throws()
    {
        var movies = Path.Combine(Path.GetTempPath(), "media", "movies");
        var tv = Path.Combine(Path.GetTempPath(), "media", "tv");
        var settings = WithRoots($"movies={movies},tv={tv}");

        Assert.Throws<ArgumentException>(() => settings.ResolveMediaPath(mountLabel: null, relativePath: "in.mkv"));
    }

    [Fact]
    public void ResolveMediaPath_RejectsTraversalOutsideRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "media", "movies");
        var settings = WithRoots($"movies={root}");

        Assert.Throws<ArgumentException>(() => settings.ResolveMediaPath("movies", "../../etc/passwd"));
    }
}
