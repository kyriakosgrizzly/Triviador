using System.Text.Json.Serialization;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using OpenTelemetry.Instrumentation.AspNetCore;
using OpenTelemetry.Metrics;
using Microsoft.AspNetCore.SignalR;
using Triviador.Domain.Questions;
using Triviador.Server;
using Triviador.Server.Auth;
using Triviador.Server.Hubs;
using Triviador.Server.Questions;
using Triviador.Server.Rooms;
using Triviador.Server.Telemetry;

var builder = WebApplication.CreateBuilder(args);

// Application Insights through OpenTelemetry (the Azure Monitor distro): requests, exceptions, logs,
// dependencies (Cosmos DB) and the game metrics in Telemetry/GameMetrics.cs. Switched on only when Azure
// gives the app a connection string (Bicep sets APPLICATIONINSIGHTS_CONNECTION_STRING), so local runs
// send nothing.
if (!string.IsNullOrWhiteSpace(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
{
    // Lets the Azure SDKs (here: Cosmos DB) report each call as a dependency in Application Insights.
    AppContext.SetSwitch("Azure.Experimental.EnableActivitySource", true);
    builder.Services.AddOpenTelemetry().UseAzureMonitor();
    builder.Services.ConfigureOpenTelemetryMeterProvider((_, metrics) => metrics.AddMeter(GameMetrics.MeterName));
    // The liveness probe hits /healthz every 30 s; keep it out of the requests table.
    builder.Services.Configure<AspNetCoreTraceInstrumentationOptions>(o =>
        o.Filter = http => !http.Request.Path.StartsWithSegments("/healthz"));
}
builder.Services.AddSingleton<GameMetrics>();

builder.Services.Configure<GameOptions>(builder.Configuration.GetSection(GameOptions.Section));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IUserIdProvider, PlayerIdProvider>();

// Sign-in: Azure Container Apps' built-in authentication (Google) in front of the app; see Auth/PlayerIdentity.cs.
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.Section));
builder.Services.AddDataProtection(); // signs the game tokens; keys live in memory (one replica, tokens refresh on reconnect)
builder.Services.AddSingleton<PlayerIdentity>();
builder.Services.AddSingleton<MatchRegistry>();
builder.Services.AddSingleton<Matchmaker>();
builder.Services.AddSingleton<LobbyRegistry>();
builder.Services.AddSingleton<ConnectionTracker>();
builder.Services.AddHostedService<GameLoop>();

// Questions: start with the embedded bank, then switch to Azure Cosmos DB once it loads (if configured).
builder.Services.Configure<CosmosOptions>(builder.Configuration.GetSection(CosmosOptions.Section));
builder.Services.AddSingleton<QuestionCatalog>();
builder.Services.AddHostedService<CosmosQuestionLoader>();

builder.Services
    .AddSignalR()
    .AddJsonProtocol(o =>
    {
        o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        // Conquest orders are a list of mixed types tagged with "kind"; accept the tag in any position.
        o.PayloadSerializerOptions.AllowOutOfOrderMetadataProperties = true;
    });

var app = builder.Build();

app.Services.GetRequiredService<GameMetrics>().Observe(
    app.Services.GetRequiredService<MatchRegistry>(),
    app.Services.GetRequiredService<ConnectionTracker>());

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapHub<GameHub>("/hubs/game");
app.MapGet("/healthz", () => Results.Ok("ok"));
// Where the questions come from right now (Embedded or CosmosDb) and how many there are. No answers.
// Who am I? Signed-in players also get a short-lived token for the game connection.
app.MapGet("/api/me", (HttpRequest request, PlayerIdentity identity) =>
{
    request.HttpContext.Response.Headers.CacheControl = "no-store";
    return identity.FromEasyAuth(request) is { } user
        ? Results.Ok(new MeDto(true, true, user.PlayerId, user.Name, user.Email, user.Provider, identity.IssueToken(user)))
        : Results.Ok(new MeDto(identity.AuthEnabled, false, null, null, null, null, null));
});
app.MapGet("/api/questions", (QuestionCatalog catalog) => Results.Ok(catalog.Stats()));

app.Run();
