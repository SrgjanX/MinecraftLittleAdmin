using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MinecraftLittleAdmin;

/// <summary>
/// A tiny WebSocket server that Minecraft Bedrock (Minecraft for Windows) connects to
/// when a player types "/connect &lt;ip&gt;:&lt;port&gt;" in chat. Once connected, we can send
/// commands to the game exactly as if the player had typed them.
/// </summary>
public sealed class MinecraftBridge : IAsyncDisposable
{
    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    private readonly int _port;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly Dictionary<string, TaskCompletionSource<CommandResult>> _pending = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private WebSocket? _socket;

    public MinecraftBridge(int port) => _port = port;

    public int Port => _port;

    public bool IsConnected => _socket is { State: WebSocketState.Open };

    /// <summary>Raised (on a background thread) when Minecraft connects or disconnects.</summary>
    public event Action<bool>? ConnectionChanged;

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Loopback, _port);
        _listener.Start();
        _ = AcceptLoopAsync(_cts.Token);
    }

    /// <summary>Sends a command (without the leading slash) and waits for Minecraft's reply.</summary>
    public async Task<CommandResult> SendCommandAsync(string commandLine)
    {
        var socket = _socket;
        if (socket is not { State: WebSocketState.Open })
            return new CommandResult(false, "Minecraft is not connected yet.");

        var requestId = Guid.NewGuid().ToString();
        var message = new JsonObject
        {
            ["header"] = new JsonObject
            {
                ["version"] = 1,
                ["requestId"] = requestId,
                ["messageType"] = "commandRequest",
                ["messagePurpose"] = "commandRequest",
            },
            ["body"] = new JsonObject
            {
                ["version"] = 1,
                ["commandLine"] = commandLine,
                ["origin"] = new JsonObject { ["type"] = "player" },
            },
        };

        var tcs = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pending) _pending[requestId] = tcs;

        try
        {
            var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
            await _sendLock.WaitAsync();
            try
            {
                await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
            }
            finally
            {
                _sendLock.Release();
            }

            var finished = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            return finished == tcs.Task
                ? tcs.Task.Result
                : new CommandResult(false, "Minecraft didn't answer in time.");
        }
        catch (Exception ex)
        {
            return new CommandResult(false, ex.Message);
        }
        finally
        {
            lock (_pending) _pending.Remove(requestId);
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(ct);
            }
            catch
            {
                return;
            }

            _ = HandleClientAsync(client, ct);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        WebSocket? socket = null;
        try
        {
            var stream = client.GetStream();
            if (!await PerformHandshakeAsync(stream, ct))
                return;

            socket = WebSocket.CreateFromStream(stream, isServer: true, subProtocol: null,
                keepAliveInterval: TimeSpan.FromSeconds(20));

            // Only one Minecraft at a time: a new connection replaces the old one.
            var previous = Interlocked.Exchange(ref _socket, socket);
            previous?.Abort();
            ConnectionChanged?.Invoke(true);

            await ReceiveLoopAsync(socket, ct);
        }
        catch
        {
            // Connection dropped; fall through to cleanup.
        }
        finally
        {
            if (socket != null && Interlocked.CompareExchange(ref _socket, null, socket) == socket)
                ConnectionChanged?.Invoke(false);
            socket?.Dispose();
        }
    }

    private static async Task<bool> PerformHandshakeAsync(NetworkStream stream, CancellationToken ct)
    {
        // Read the HTTP upgrade request headers (ends with a blank line).
        var buffer = new byte[8192];
        var total = 0;
        string request;
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct);
            if (read == 0)
                return false;
            total += read;
            request = Encoding.ASCII.GetString(buffer, 0, total);
            if (request.Contains("\r\n\r\n"))
                break;
            if (total == buffer.Length)
                return false;
        }

        string? key = null;
        foreach (var line in request.Split("\r\n"))
        {
            var colon = line.IndexOf(':');
            if (colon > 0 && line[..colon].Trim().Equals("Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase))
                key = line[(colon + 1)..].Trim();
        }

        if (key == null)
        {
            var notWs = Encoding.ASCII.GetBytes(
                "HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(notWs, ct);
            return false;
        }

        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + WebSocketGuid)));
        var response = Encoding.ASCII.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {accept}\r\n\r\n");
        await stream.WriteAsync(response, ct);
        return true;
    }

    private async Task ReceiveLoopAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();

        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                return;
            }

            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage)
                continue;

            HandleMessage(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length));
            message.SetLength(0);
        }
    }

    private void HandleMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("header", out var header) ||
                !header.TryGetProperty("requestId", out var idElement))
                return;

            var id = idElement.GetString();
            if (id == null)
                return;

            TaskCompletionSource<CommandResult>? tcs;
            lock (_pending) _pending.TryGetValue(id, out tcs);
            if (tcs == null)
                return;

            var statusCode = 0;
            string message = "";
            if (root.TryGetProperty("body", out var body))
            {
                if (body.TryGetProperty("statusCode", out var code) && code.ValueKind == JsonValueKind.Number)
                    statusCode = code.GetInt32();
                if (body.TryGetProperty("statusMessage", out var msg) && msg.ValueKind == JsonValueKind.String)
                    message = msg.GetString() ?? "";
            }

            tcs.TrySetResult(new CommandResult(statusCode >= 0, message));
        }
        catch (JsonException)
        {
            // Ignore anything that isn't JSON we understand.
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _listener?.Stop();
        var socket = Interlocked.Exchange(ref _socket, null);
        if (socket != null)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Bye!", timeout.Token);
            }
            catch
            {
                socket.Abort();
            }
            socket.Dispose();
        }
    }
}

public sealed record CommandResult(bool Success, string Message);
