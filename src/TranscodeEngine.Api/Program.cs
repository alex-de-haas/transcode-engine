// Transcode Engine control API.
//
// An ffmpeg-backed batch transcoder exposed over an HTTP/SSE control API for other Hosty apps to drive
// re-encoding jobs over a dependency. Hardware acceleration: VAAPI in the docker runtime via a passed-through
// /dev/dri device (granted by the Hosty manifest's `devices`); VideoToolbox when run natively on macOS via
// the localCommand runtime. See README.

using TranscodeEngine.Api.Api;
using TranscodeEngine.Api.Realtime;
using TranscodeEngine.Api.Transcoding;

var builder = WebApplication.CreateBuilder(args);

// Under the localCommand runtime (e.g. running natively on macOS for VideoToolbox) Core assigns a loopback
// port and injects it as HOSTY_PORT_CONTROL; bind exactly that. The docker image sets ASPNETCORE_URLS, so
// this is skipped there. Do NOT fall back to the ambient PORT env — under localCommand it can be inherited
// from Core's own environment, which would make Kestrel try to bind Core's port.
if (string.IsNullOrWhiteSpace(builder.Configuration["ASPNETCORE_URLS"]) &&
    int.TryParse(builder.Configuration["HOSTY_PORT_CONTROL"], out var controlPort))
{
    builder.WebHost.UseUrls($"http://localhost:{controlPort}");
}

builder.Services.AddSingleton(TranscodeEngineSettings.FromConfiguration(builder.Configuration, builder.Environment.ContentRootPath));

// The engine is a single instance that is also the hosted service (runs the job workers) and the
// ITranscodeEngine the API + broadcaster resolve.
builder.Services.AddSingleton<FfmpegTranscodeEngine>();
builder.Services.AddSingleton<ITranscodeEngine>(sp => sp.GetRequiredService<FfmpegTranscodeEngine>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<FfmpegTranscodeEngine>());

builder.Services.AddSingleton<TranscodeEventStream>();
builder.Services.AddHostedService<TranscodeProgressBroadcaster>();

var app = builder.Build();

// Liveness — also used by a consumer to gate readiness.
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

// Detected hardware accelerators (a consumer can surface what the host actually offers).
app.MapGet("/hardware", (TranscodeEngineSettings settings) => Results.Ok(HardwareProbe.Detect(settings)));

app.MapTranscodeEndpoints();

app.Run();
