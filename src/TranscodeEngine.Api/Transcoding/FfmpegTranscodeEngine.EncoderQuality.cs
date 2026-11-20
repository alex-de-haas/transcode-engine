using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;

namespace TranscodeEngine.Api.Transcoding;

public sealed partial class FfmpegTranscodeEngine
{
    private static readonly TimeSpan EncoderQualityProbeTimeout = TimeSpan.FromSeconds(8);
    private readonly ConcurrentDictionary<(TranscodeVideoCodec, TranscodeHardware), Lazy<Task<bool>>> _encoderQualityProbes = new();

    internal async Task<IReadOnlyList<EncoderQualityOption>> GetEncoderQualityOptionsAsync(HardwareStatus status, CancellationToken cancellationToken)
    {
        var hardwareChoices = new[] { TranscodeHardware.None, TranscodeHardware.Vaapi, TranscodeHardware.Amf, TranscodeHardware.VideoToolbox };
        var probes = from hardware in hardwareChoices
                     where HardwareProbe.IsEncoderAvailable(status, hardware)
                     from codec in Enum.GetValues<TranscodeVideoCodec>()
                     let option = QualityLevels.EncoderOption(codec, hardware)
                     select CheckAsync(codec, hardware, option);
        var options = await Task.WhenAll(probes).WaitAsync(cancellationToken);
        return options.OfType<EncoderQualityOption>().ToArray();
    }

    private async Task<EncoderQualityOption?> CheckAsync(
        TranscodeVideoCodec codec, TranscodeHardware hardware, EncoderQualityOption option)
    {
        var probe = _encoderQualityProbes.GetOrAdd((codec, hardware), _ => new Lazy<Task<bool>>(
            () => ProbeEncoderQualityAsync(codec, hardware, option.DefaultValue),
            LazyThreadSafetyMode.ExecutionAndPublication));
        // A busy device or a transient driver fault can clear. Keep successful probes for the engine
        // process lifetime, and let a later availability request retry an unsuccessful probe.
        if (await probe.Value)
            return option;
        _encoderQualityProbes.TryRemove(new KeyValuePair<(TranscodeVideoCodec, TranscodeHardware), Lazy<Task<bool>>>((codec, hardware), probe));
        return null;
    }

    private async Task<bool> ProbeEncoderQualityAsync(TranscodeVideoCodec codec, TranscodeHardware hardware, int value)
    {
        // Check a small generated encode through the same rate-control flags as a real job. Encoder
        // presence alone would, for example, offer VideoToolbox quality mode on an Intel Mac.
        var args = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error" };
        if (hardware == TranscodeHardware.Vaapi)
            args.AddRange(["-vaapi_device", _settings.VaapiDevice]);
        args.AddRange(["-f", "lavfi", "-i", "color=black:s=256x256:r=25:d=0.12"]);
        AddVideoEncode(args, new TranscodeJobRequest("", null, codec, hardware, null, EncoderQuality: value), hardware, sourcePixelFormat: null);
        args.AddRange(["-f", "null", "-"]);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(_settings.FfmpegPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);
        try
        {
            if (!process.Start())
                return false;
            using var timeout = new CancellationTokenSource(EncoderQualityProbeTimeout);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            await Task.WhenAll(process.WaitForExitAsync(timeout.Token), stdout, stderr);
            if (process.ExitCode == 0)
                return true;
            _logger.LogDebug("Native quality probe for {Encoder} failed: {Error}", EncoderName(codec, hardware), await stderr);
        }
        catch (OperationCanceledException)
        {
            TryKillProcess(process);
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try { await process.WaitForExitAsync(cleanup.Token); }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Could not confirm FFmpeg exit after timing out the probe for {Encoder}.", EncoderName(codec, hardware));
            }
            _logger.LogDebug("Native quality probe for {Encoder} timed out.", EncoderName(codec, hardware));
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            _logger.LogDebug(exception, "Could not probe native quality for {Encoder}.", EncoderName(codec, hardware));
        }
        return false;
    }

    private void FailEncoderSelection(TranscodeJob job, string error)
    {
        job.Fail(error);
        JobFailed?.Invoke(this, job.JobId);
        _logger.LogWarning("Job {JobId}: {Error}", job.JobId, error);
    }
}
