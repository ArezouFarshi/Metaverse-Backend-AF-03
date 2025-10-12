// ✅ Final Backend Code with Correct Color Logic for Lifecycle States
using System;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Numerics;
using Nethereum.Web3;
using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Contracts;
using Nethereum.Hex.HexTypes;
using Nethereum.RPC.Eth.DTOs;

[Event("PanelEventAdded")]
public class PanelEventAddedDTO : IEventDTO
{
    [Parameter("string", "panelId", 1, false)]
    public string? PanelId { get; set; }

    [Parameter("string", "eventType", 2, false)]
    public string? EventType { get; set; }

    [Parameter("string", "faultType", 3, false)]
    public string? FaultType { get; set; }

    [Parameter("string", "faultSeverity", 4, false)]
    public string? FaultSeverity { get; set; }

    [Parameter("string", "actionTaken", 5, false)]
    public string? ActionTaken { get; set; }

    [Parameter("bytes32", "eventHash", 6, false)]
    public byte[]? EventHash { get; set; }

    [Parameter("address", "validatedBy", 7, false)]
    public string? ValidatedBy { get; set; }

    [Parameter("uint256", "timestamp", 8, false)]
    public BigInteger Timestamp { get; set; }
}

class Program
{
    static ConcurrentDictionary<Guid, WebSocket> clients = new();
    static ConcurrentDictionary<string, string> panelStates = new();

    static async Task Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        string port = Environment.GetEnvironmentVariable("PORT") ?? "10000";
        string infuraUrl = Environment.GetEnvironmentVariable("INFURA_URL")
            ?? "https://sepolia.infura.io/v3/51bc36040f314e85bf103ff18c570993";
        string contractAddress = Environment.GetEnvironmentVariable("CONTRACT_ADDRESS")
            ?? "0x59B649856d8c5Fb6991d30a345f0b923eA91a3f7";
        int pollMs = int.TryParse(Environment.GetEnvironmentVariable("POLL_MS"), out var ms) ? ms : 10000;

        Console.WriteLine($"\n⚙️  Config:");
        Console.WriteLine($"   • PORT={port}");
        Console.WriteLine($"   • INFURA_URL={infuraUrl}");
        Console.WriteLine($"   • CONTRACT_ADDRESS={contractAddress}");
        Console.WriteLine($"   • POLL_MS={pollMs}\n");

        await Task.WhenAll(
            StartWebSocketAndHttpServer(port),
            StartBlockchainListener(infuraUrl, contractAddress, pollMs)
        );
    }

    static async Task StartWebSocketAndHttpServer(string port)
    {
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://+:{port}/");
        listener.Start();
        Console.WriteLine($"✅ Server listening on http://0.0.0.0:{port}/\n");

        while (true)
        {
            HttpListenerContext context = await listener.GetContextAsync();

            if (context.Request.IsWebSocketRequest)
            {
                var wsContext = await context.AcceptWebSocketAsync(null);
                var id = Guid.NewGuid();
                clients[id] = wsContext.WebSocket;
                Console.WriteLine($"🌐 WebSocket client connected: {id}");

                _ = Task.Run(async () =>
                {
                    var socket = wsContext.WebSocket;
                    var buffer = new byte[1024];

                    try
                    {
                        while (socket.State == WebSocketState.Open)
                        {
                            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                            if (result.MessageType == WebSocketMessageType.Close) break;
                        }
                    }
                    finally
                    {
                        clients.TryRemove(id, out _);
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed", CancellationToken.None);
                        Console.WriteLine($"❌ WebSocket client disconnected: {id}");
                    }
                });
            }
            else
            {
                await HandleHttpRequest(context);
            }
        }
    }

    static async Task HandleHttpRequest(HttpListenerContext context)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath?.TrimEnd('/') ?? "/";

            if (context.Request.HttpMethod == "GET" && path == "/api/visibility")
            {
                var json = JsonSerializer.Serialize(panelStates);
                await WriteJson(context, json);
            }
            else
            {
                var msg = Encoding.UTF8.GetBytes("👋 Metaverse Backend is running!");
                context.Response.ContentType = "text/plain";
                context.Response.ContentLength64 = msg.Length;
                await context.Response.OutputStream.WriteAsync(msg, 0, msg.Length);
                context.Response.OutputStream.Close();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("HTTP error: " + ex.Message);
            context.Response.StatusCode = 500;
            context.Response.Close();
        }
    }

    static async Task WriteJson(HttpListenerContext context, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
        context.Response.OutputStream.Close();
    }

    static async Task StartBlockchainListener(string infuraUrl, string contractAddress, int pollMs)
    {
        var web3 = new Web3(infuraUrl);
        var eventHandler = web3.Eth.GetEvent<PanelEventAddedDTO>(contractAddress);

        var lastBlock = await web3.Eth.Blocks.GetBlockNumber.SendRequestAsync();

        while (true)
        {
            var currentBlock = await web3.Eth.Blocks.GetBlockNumber.SendRequestAsync();

            if (currentBlock.Value > lastBlock.Value)
            {
                var filter = eventHandler.CreateFilterInput(
                    new BlockParameter(new HexBigInteger(lastBlock.Value + 1)),
                    new BlockParameter(currentBlock)
                );

                var logs = await eventHandler.GetAllChangesAsync(filter);

                foreach (var ev in logs)
                {
                    var e = ev.Event;
                    string hash = e.EventHash != null ? "0x" + BitConverter.ToString(e.EventHash).Replace("-", "").ToLower() : "0x";

                    if (!string.IsNullOrEmpty(e.PanelId) && !string.IsNullOrEmpty(e.EventType))
                    {
                        // ✅ Update dictionary with lifecycle event type
                        panelStates[e.PanelId] = e.EventType.ToLower();
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

                    await BroadcastAsync(payload);
                }

                lastBlock = currentBlock;
            }

            await Task.Delay(pollMs);
        }
    }

    static async Task BroadcastAsync(string message)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        var toRemove = new ConcurrentBag<Guid>();

        foreach (var kvp in clients)
        {
            try
            {
                if (kvp.Value.State == WebSocketState.Open)
                    await kvp.Value.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
                else
                    toRemove.Add(kvp.Key);
            }
            catch
            {
                toRemove.Add(kvp.Key);
            }
        }

        foreach (var id in toRemove)
        {
            clients.TryRemove(id, out _);
            Console.WriteLine($"🧹 Cleaned WebSocket: {id}");
        }
    }
}
