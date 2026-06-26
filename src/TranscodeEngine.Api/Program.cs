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

// Bind the port for the runtime profile. Under localCommand (e.g. running natively on macOS for
// VideoToolbox) Core assigns a loopback port and injects it as HOSTY_PORT_CONTROL — bind exactly that,
// overriding any ASPNETCORE_URLS / PORT the child inherited from Core's own environment (Core is an ASP.NET
// app on its own port, so those inherited values would otherwise make Kestrel try to bind Core's port).
// Under docker the image sets DOTNET_RUNNING_IN_CONTAINER=true + ASPNETCORE_URLS, so leave Kestrel's default
// handling alone. Mirrors media-server's HostyKestrel (which keys off the container flag, not ASPNETCORE_URLS).
if (!string.Equals(builder.Configuration["DOTNET_RUNNING_IN_CONTAINER"], "true", StringComparison.OrdinalIgnoreCase) &&
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
