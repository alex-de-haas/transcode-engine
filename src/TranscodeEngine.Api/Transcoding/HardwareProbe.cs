namespace TranscodeEngine.Api.Transcoding;

/// <summary>Best-effort report of the hardware the engine can use, surfaced at <c>GET /hardware</c>. This is
/// informational only (a consumer can show what the host offers); it is not a correctness gate.</summary>
/// <param name="VaapiAvailable">Whether a VAAPI render node is present (the manifest's <c>/dev/dri</c>
/// passthrough worked and the host exposes a render device).</param>
/// <param name="VaapiDevice">The configured render node, when present.</param>
/// <param name="RenderDevices">All <c>/dev/dri/renderD*</c> nodes visible inside the container.</param>
/// <param name="VideoToolboxAvailable">Whether VideoToolbox is reachable — true only when the engine runs
/// natively on macOS (never inside the Linux docker container).</param>
/// <param name="AmfAvailable">Whether the AMD AMF runtime is reachable — true only when the engine runs
/// natively on Windows and the AMD driver's <c>amfrt64.dll</c> is present (never inside the Linux docker
/// container).</param>
public sealed record HardwareStatus(
    bool VaapiAvailable,
    string? VaapiDevice,
    IReadOnlyList<string> RenderDevices,
    bool VideoToolboxAvailable,
    bool AmfAvailable,
    DateTimeOffset CheckedAt);

/// <summary>Inspects the passed-through DRI devices to report VAAPI availability without spawning a process.</summary>
public static class HardwareProbe
{
    public static HardwareStatus Detect(TranscodeEngineSettings settings)
    {
        var renderDevices = EnumerateRenderNodes();
        var configured = settings.VaapiDevice;
        var available = File.Exists(configured) || renderDevices.Count > 0;
        return new HardwareStatus(
            available,
            available ? (File.Exists(configured) ? configured : renderDevices[0]) : null,
            renderDevices,
            OperatingSystem.IsMacOS(),
            AmfRuntimeAvailable(),
            DateTimeOffset.UtcNow);
    }

    /// <summary>True when the AMD AMF runtime (<c>amfrt64.dll</c>, shipped by the Adrenalin driver) is present
    /// on a native Windows host — the signal that the <c>*_amf</c> encoders can initialise. The docker runtime
    /// is Linux, so this is always false there.</summary>
    private static bool AmfRuntimeAvailable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
            return system32.Length > 0 && File.Exists(Path.Combine(system32, "amfrt64.dll"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static IReadOnlyList<string> EnumerateRenderNodes()
    {
        const string driDir = "/dev/dri";
        if (!Directory.Exists(driDir))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateFiles(driDir, "renderD*")
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
