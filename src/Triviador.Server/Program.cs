using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR;
using Triviador.Domain.Questions;
using Triviador.Server;
using Triviador.Server.Hubs;
using Triviador.Server.Questions;
using Triviador.Server.Rooms;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<GameOptions>(builder.Configuration.GetSection(GameOptions.Section));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IUserIdProvider, PlayerIdProvider>();
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

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapHub<GameHub>("/hubs/game");
app.MapGet("/healthz", () => Results.Ok("ok"));
// Where the questions come from right now (Embedded or CosmosDb) and how many there are. No answers.
app.MapGet("/api/questions", (QuestionCatalog catalog) => Results.Ok(catalog.Stats()));

app.Run();
