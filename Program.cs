using System.Collections.Concurrent;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Nethereum.Web3;
using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Contracts;
using Nethereum.Hex.HexTypes;
using Nethereum.RPC.Eth.DTOs;

[Event("PanelEventAdded")]
public class PanelEventAddedDTO : IEventDTO
{
    [Parameter("string", "panelId", 1, false)] public string? PanelId { get; set; }
    [Parameter("string", "eventType", 2, false)] public string? EventType { get; set; }
    [Parameter("string", "faultType", 3, false)] public string? FaultType { get; set; }
    [Parameter("string", "faultSeverity", 4, false)] public string? FaultSeverity { get; set; }
    [Parameter("string", "actionTaken", 5, false)] public string? ActionTaken { get; set; }
    [Parameter("bytes32", "eventHash", 6, false)] public byte[]? EventHash { get; set; }
    [Parameter("address", "validatedBy", 7, false)] public string? ValidatedBy { get; set; }
    [Parameter("uint256", "timestamp", 8, false)] public BigInteger Timestamp { get; set; }
}

var builder = WebApplication.CreateBuilder(args);

// Bind to Render/hosting port
var port = Environment.GetEnvironmentVariable("PORT") ?? "10000";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

// CORS (allow Spatial and your site; adjust as needed)
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .AllowAnyOrigin()
    .AllowAnyHeader()
    .AllowAnyMethod()
));

builder.Services.AddLogging();

// Shared state
var clients = new ConcurrentDictionary<Guid, System.Net.WebSockets.WebSocket>();
var panelStates = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

// App
var app = builder.Build();
app.UseCors();
app.UseWebSockets();

app.MapGet("/", () => Results.Text("👋 Metaverse Backend is running!"));
app.MapGet("/api/visibility", (HttpContext ctx) =>
{
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
    try
    {
        var buffer = new byte[1024];
        while (socket.State == System.Net.WebSockets.WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), context.RequestAborted);
            if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close) break;
            // (Optional) handle messages from clients here
        }
    }
    finally
    {
        clients.TryRemove(id, out _);
        if (socket.State != System.Net.WebSockets.WebSocketState.Closed)
            await socket.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "Closed", context.RequestAborted);
    }
});

// ---- Blockchain listener background task (same logic, Kestrel-friendly) ----
var infuraUrl = Environment.GetEnvironmentVariable("INFURA_URL")
    ?? "https://sepolia.infura.io/v3/51bc36040f314e85bf103ff18c570993";
var contractAddress = Environment.GetEnvironmentVariable("CONTRACT_ADDRESS")
    ?? "0x59B649856d8c5Fb6991d30a345f0b923eA91a3f7";
var pollMs = int.TryParse(Environment.GetEnvironmentVariable("POLL_MS"), out var ms) ? ms : 10000;

_ = Task.Run(async () =>
{
    var logger = app.Logger;
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
                // (Optional) confirmation depth: process up to current-0 for demo
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

                    var payload = JsonSerializer.Serialize(new
                    {
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

// ---------------- helpers ----------------
static async Task BroadcastAsync(string message, ConcurrentDictionary<Guid, System.Net.WebSockets.WebSocket> clients)
{
    var bytes = Encoding.UTF8.GetBytes(message);
    var toRemove = new List<Guid>();

    foreach (var kv in clients)
    {
        try
        {
            if (kv.Value.State == System.Net.WebSockets.WebSocketState.Open)
                await kv.Value.SendAsync(new ArraySegment<byte>(bytes), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
            else
                toRemove.Add(kv.Key);
        }
        catch
        {
            toRemove.Add(kv.Key);
        }
    }

    foreach (var id in toRemove)
    {
        if (clients.TryRemove(id, out var ws))
        {
            try { await ws.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "Cleanup", CancellationToken.None); } catch { }
        }
    }
}

static string MapEventTypeToStatus(string? eventType, string? faultSeverity)
{
    if (string.IsNullOrWhiteSpace(eventType)) return "blue";
    var t = eventType.Trim().ToLowerInvariant();

    if (t is "installed" or "ok" or "resolved" or "maintenancecompleted")
        return "green";
    if (t is "warning" or "degraded")
        return "yellow";
    if (t is "fault" or "error" or "failed" or "critical")
        return "red";
    if (t is "systemerror" or "oraclemismatch" or "invalidsignature")
        return "purple";
    if (t is "notinstalled" or "pending")
        return "grey";

    return "blue";
}
