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

// Bind to Render port
var builder = WebApplication.CreateBuilder(args);
var port = Environment.GetEnvironmentVariable("PORT") ?? "10000";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

// CORS for Spatial/web builds
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .AllowAnyOrigin()
    .AllowAnyHeader()
    .AllowAnyMethod()
));

builder.Services.AddLogging();

var app = builder.Build();
app.UseCors();
app.UseWebSockets();

// Shared state
var clients = new ConcurrentDictionary<Guid, WebSocket>();
var panelStates = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

// HTTP endpoints
app.MapGet("/", () => Results.Text("👋 Metaverse Backend is running!"));
app.MapGet("/api/visibility", (HttpContext ctx) => {
    ctx.Response.Headers.CacheControl = "no-store";
    return Results.Json(panelStates);
});

// WebSocket endpoint
app.Map("/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
    var socket = await context.WebSockets.AcceptWebSocketAsync();
    var id = Guid.NewGuid();
    clients[id] = socket;

    try {
        var buffer = new byte[1024];
        while (socket.State == WebSocketState.Open) {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), context.RequestAborted);
            if (result.MessageType == WebSocketMessageType.Close) break;
        }
    }
    finally {
        clients.TryRemove(id, out _);
        if (socket.State != WebSocketState.Closed)
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed", context.RequestAborted);
    }
});

// ----- Blockchain listener in background -----
var infuraUrl = Environment.GetEnvironmentVariable("INFURA_URL")
    ?? "https://sepolia.infura.io/v3/51bc36040f314e85bf103ff18c570993";
var contractAddress = Environment.GetEnvironmentVariable("CONTRACT_ADDRESS")
    ?? "0x59B649856d8c5Fb6991d30a345f0b923eA91a3f7";
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
                var to   = new BlockParameter(current);
                var filter = handler.CreateFilterInput(from, to);
                var logs = await handler.GetAllChangesAsync(filter);

                foreach (var ch in logs)
                {
                    var e = ch.Event;
                    string hash = e.EventHash != null ? "0x" + BitConverter.ToString(e.EventHash).Replace("-", "").ToLower() : "0x";

                    if (!string.IsNullOrWhiteSpace(e.PanelId) && !string.IsNullOrWhiteSpace(e.EventType))
                    {
                        var status = MapEventTypeToStatus(e.EventType, e.FaultSeverity);
                        panelStates[e.PanelId] = status;
                    }

                    var payload = JsonSerializer.Serialize(new {
                        panelId = e.PanelId,
                        eventType = e.EventType,
                        faultType = e.FaultType,
                        faultSeverity = e.FaultSeverity,
                        actionTaken = e.ActionTaken,
                        eventHash = hash,
                        validatedBy = e.ValidatedBy,
                        timestamp = e.Timestamp.ToString()
                    });

                    await BroadcastAsync(payload, clients);
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

// ---------- local helpers (ok with top-level) ----------
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

static string MapEventTypeToStatus(string? eventType, string? faultSeverity)
{
    if (string.IsNullOrWhiteSpace(eventType)) return "blue";
    var t = eventType.Trim().ToLowerInvariant();

    if (t is "installed" or "ok" or "resolved" or "maintenancecompleted") return "green";
    if (t is "warning" or "degraded") return "yellow";
    if (t is "fault" or "error" or "failed" or "critical") return "red";
    if (t is "systemerror" or "oraclemismatch" or "invalidsignature") return "purple";
    if (t is "notinstalled" or "pending") return "grey";
    return "blue";
}
