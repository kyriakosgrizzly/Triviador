using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR;
using Triviador.Server;
using Triviador.Server.Hubs;
using Triviador.Server.Rooms;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<GameOptions>(builder.Configuration.GetSection(GameOptions.Section));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IUserIdProvider, PlayerIdProvider>();
builder.Services.AddSingleton<MatchRegistry>();
builder.Services.AddSingleton<Matchmaker>();
builder.Services.AddHostedService<GameLoop>();

builder.Services
    .AddSignalR()
    .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapHub<GameHub>("/hubs/game");
app.MapGet("/healthz", () => Results.Ok("ok"));

app.Run();
