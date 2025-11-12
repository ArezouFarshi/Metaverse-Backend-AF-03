using System.Collections.Concurrent;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Nethereum.Web3;
using Nethereum.Contracts;
using Nethereum.Hex.HexTypes;
using Nethereum.RPC.Eth.DTOs;

// ----- Build Web App -----
var builder = WebApplication.CreateBuilder(args);
var port = Environment.GetEnvironmentVariable("PORT") ?? "10000";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .AllowAnyOrigin()
    .AllowAnyHeader()
    .AllowAnyMethod()
));

builder.Services.AddLogging();

var app = builder.Build();
app.UseCors();
app.UseWebSockets();

// ----- Shared State -----
var clients = new ConcurrentDictionary<Guid, WebSocket>();
var panelStates = new ConcurrentDictionary<string, object>(StringComparer.OrdinalIgnoreCase);

// ----- HTTP Endpoints -----
app.MapGet("/", () => Results.Text("✅ Metaverse Backend is running!"));
app.MapGet("/api/visibility", (HttpContext ctx) =>
{
    ctx.Response.Headers.CacheControl = "no-store";
    return Results.Json(panelStates);
});

// ----- WebSocket Endpoint -----
app.Map("/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
    var socket = await context.WebSockets.AcceptWebSocketAsync();
    var id = Guid.NewGuid();
    clients[id] = socket;

    try
    {
        var buffer = new byte[1024];
        while (socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), context.RequestAborted);
            if (result.MessageType == WebSocketMessageType.Close) break;
        }
    }
    finally
    {
        clients.TryRemove(id, out _);
        if (socket.State != WebSocketState.Closed)
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed", context.RequestAborted);
    }
});

// ----- Blockchain Listener -----
var infuraUrl = Environment.GetEnvironmentVariable("INFURA_URL")
    ?? "https://sepolia.infura.io/v3/6ad85a144d0445a3b181add73f6a55d9";
var contractAddress = Environment.GetEnvironmentVariable("CONTRACT_ADDRESS")
    ?? "0xF2dCCAddE9dEe3ffF26C98EC63e2c44E08B4C65c";
var pollMs = int.TryParse(Environment.GetEnvironmentVariable("POLL_MS"), out var ms) ? ms : 10000;

_ = Task.Run(async () =>
{
    var web3 = new Web3(infuraUrl);
    var handler = web3.Eth.GetEvent<PanelEventAddedDTO>(contractAddress);
    var lastBlock = await web3.Eth.Blocks.GetBlockNumber.SendRequestAsync();

    while (true)
    {
        try
        {
            var current = await web3.Eth.Blocks.GetBlockNumber.SendRequestAsync();
            if (current.Value > lastBlock.Value)
            {
                var from = new BlockParameter(new HexBigInteger(lastBlock.Value + 1));
                var to = new BlockParameter(current);
                var filter = handler.CreateFilterInput(from, to);
                var logs = await handler.GetAllChangesAsync(filter);

                foreach (var ch in logs)
                {
                    var e = ch.Event;
                    if (string.IsNullOrWhiteSpace(e.PanelId)) continue;

                    // Update current panel state (ID_x_x_x dynamic)
                    var panelInfo = new
                    {
                        color = e.Color ?? "blue",
                        status = e.Status ?? "Unknown",
                        ok = e.Ok,
                        prediction = e.Prediction.ToString(),
                        reason = e.Reason ?? "",
                        timestamp = e.Timestamp.ToString()
                    };

                    panelStates[e.PanelId] = panelInfo;

                    // Broadcast full payload
                    var payload = JsonSerializer.Serialize(new
                    {
                        panelId = e.PanelId,
                        color = e.Color ?? "blue",
                        status = e.Status ?? "Unknown",
                        ok = e.Ok,
                        prediction = e.Prediction.ToString(),
                        reason = e.Reason ?? "",
                        timestamp = e.Timestamp.ToString()
                    });

                    await BroadcastAsync(payload, clients);
                    app.Logger.LogInformation($"Event received for {e.PanelId}: {e.Color}/{e.Status}");
                }

                lastBlock = current;
            }
        }
        catch (Exception ex)
        {
            app.Logger.LogError(ex, "Blockchain polling error");
        }

        await Task.Delay(pollMs);
    }
});

app.Run();

// ----- Helper Methods -----
static async Task BroadcastAsync(string message, ConcurrentDictionary<Guid, WebSocket> clients)
{
    var bytes = Encoding.UTF8.GetBytes(message);
    var toRemove = new List<Guid>();

    foreach (var kv in clients)
    {
        try
        {
            if (kv.Value.State == WebSocketState.Open)
                await kv.Value.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            else
                toRemove.Add(kv.Key);
        }
        catch { toRemove.Add(kv.Key); }
    }

    foreach (var id in toRemove)
    {
        if (clients.TryRemove(id, out var ws))
        {
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Cleanup", CancellationToken.None); } catch { }
        }
    }
}
