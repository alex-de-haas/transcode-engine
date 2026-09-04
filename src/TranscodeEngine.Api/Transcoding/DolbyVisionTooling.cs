using System.Diagnostics;
using System.Text.RegularExpressions;

namespace TranscodeEngine.Api.Transcoding;

/// <summary>
/// The external tools a Dolby Vision profile 7 → 8.1 conversion runs on, surfaced under <c>tools</c> at
/// <c>GET /hardware</c>. <see cref="DolbyVisionConversion"/> is the flag a consumer gates its UI on; the two
/// versions are informational. An engine without the tools refuses the option rather than silently copying,
/// which is the same honesty <c>effectiveHardware</c> keeps about encoders.
/// </summary>
/// <param name="DolbyVisionConversion">Whether <c>dovi_tool</c>, <c>mkvmerge</c> and <c>mkvextract</c> are all
/// reachable, so <c>dolbyVision: toProfile81</c> is accepted.</param>
/// <param name="DoviTool">The <c>dovi_tool</c> version, or null when it is not found.</param>
/// <param name="Mkvtoolnix">The MKVToolNix version (<c>mkvmerge --version</c>), or null when it is not found.</param>
public sealed record ToolingStatus(bool DolbyVisionConversion, string? DoviTool, string? Mkvtoolnix);

/// <summary>Finds the conversion tools on the host without spawning anything, and describes their versions
/// when asked to — the one place a process is started, bounded and best-effort.</summary>
public static partial class DolbyVisionTooling
{
    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Whether every tool a conversion needs resolves to an existing file. A PATH scan and three
    /// <c>File.Exists</c> calls — cheap enough to run per request, like <see cref="HardwareProbe.Detect"/>.</summary>
    public static bool Available(TranscodeEngineSettings settings) =>
        Locate(settings.DoviToolPath) is not null &&
        Locate(settings.MkvmergePath) is not null &&
        Locate(settings.MkvextractPath) is not null;

    /// <summary>The full report for <c>GET /hardware</c>: availability plus each tool's version. Spawns the tools
    /// once each with <c>--version</c>; a tool that is missing, hangs or answers nonsense reports a null
    /// version, never a failed request.</summary>
    public static ToolingStatus Describe(TranscodeEngineSettings settings)
    {
        var doviTool = Locate(settings.DoviToolPath);
        var mkvmerge = Locate(settings.MkvmergePath);
        var mkvextract = Locate(settings.MkvextractPath);
        return new ToolingStatus(
            doviTool is not null && mkvmerge is not null && mkvextract is not null,
            doviTool is null ? null : ParseVersion(RunForVersion(doviTool)),
            mkvmerge is null ? null : ParseVersion(RunForVersion(mkvmerge)));
    }

    /// <summary>Resolves a configured tool to a file: a value with a directory part is checked as it stands, a
    /// bare name is looked up on the process PATH (with <c>.exe</c> tried too on Windows). Null when nothing
    /// exists there.</summary>
    internal static string? Locate(string command) =>
        Locate(command, Environment.GetEnvironmentVariable("PATH"));

    internal static string? Locate(string command, string? pathVariable)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var candidates = OperatingSystem.IsWindows() && !Path.HasExtension(command)
            ? new[] { command, command + ".exe" }
            : [command];

        if (Path.IsPathRooted(command) || command.Contains(Path.DirectorySeparatorChar) || command.Contains(Path.AltDirectorySeparatorChar))
        {
            return candidates.FirstOrDefault(File.Exists);
        }

        foreach (var directory in (pathVariable ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var candidate in candidates)
            {
                var path = Path.Combine(directory, candidate);
                if (File.Exists(path))
                {
                    return path;
                }
            }
        }

        return null;
    }

    /// <summary>The first dotted number in a tool's <c>--version</c> banner — <c>dovi_tool 2.3.3</c> → 2.3.3,
    /// <c>mkvmerge v81.0 ('Unmarked') 64-bit</c> → 81.0. Null when there is none.</summary>
    internal static string? ParseVersion(string? banner) =>
        banner is { Length: > 0 } && VersionPattern().Match(banner) is { Success: true } match
            ? match.Groups["version"].Value
            : null;

    [GeneratedRegex(@"(?<![\w.])v?(?<version>\d+(?:\.\d+)+)")]
    private static partial Regex VersionPattern();

    private static string? RunForVersion(string executable)
    {
        try
        {
            var psi = new ProcessStartInfo(executable)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // The tools write UTF-8 to a pipe on every platform; the default on Windows is the console's
                // code page, which would turn a banner's non-ASCII characters into mojibake.
                StandardOutputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                StandardErrorEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("--version");

            using var process = Process.Start(psi);
            if (process is null)
            {
                return null;
            }

            // Drain both pipes before waiting so a chatty tool cannot block on a full one.
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit((int)VersionTimeout.TotalMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception) { /* Already gone. */ }
                return null;
            }

            var banner = stdout.Result;
            return banner.Length > 0 ? banner : stderr.Result;
        }
        catch (Exception)
        {
            // Informational only: a tool that cannot be run reports no version, and never fails the request.
            return null;
        }
    }
}
