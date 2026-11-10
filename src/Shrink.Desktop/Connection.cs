using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Shrink.Desktop;

public sealed class Connection(Configuration config, Controller controller, Journal journal)
{
    public string Status { get; private set; } = "未连接";
    public static async Task Pair(Configuration config, string endpoint, string code)
    {
        var uri = new Uri(endpoint);
        if (uri.Scheme != "https" && !(uri.IsLoopback && uri.Scheme == "http")) throw new ArgumentException("HTTPS is required");
        using var client = new HttpClient();
        var response = await client.PostAsJsonAsync(endpoint.TrimEnd('/') + "/pair", new { code, name = config.Name });
        response.EnsureSuccessStatusCode();
        var data = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        config.Server = endpoint.TrimEnd('/');
        config.DeviceId = data["device_id"]!.GetValue<string>();
        config.SetToken(data["token"]!.GetValue<string>());
        config.Save();
    }
    public async Task Run(CancellationToken stop)
    {
        var backoff = 1;
        while (!stop.IsCancellationRequested)
        {
            if (config.DeviceId == "") { Status = "尚未配对"; await Task.Delay(1000, stop); continue; }
            try
            {
                using var socket = new ClientWebSocket();
                socket.Options.SetRequestHeader("Authorization", "Bearer " + config.Token);
                socket.Options.SetRequestHeader("X-Device-Id", config.DeviceId);
                var url = new UriBuilder(config.Server + "/connect") { Scheme = config.Server.StartsWith("https:") ? "wss" : "ws" };
                await socket.ConnectAsync(url.Uri, stop);
                Status = "在线"; backoff = 1;
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop);
                var sendLock = new SemaphoreSlim(1);
                async Task Send(JsonNode body)
                {
                    await sendLock.WaitAsync(linked.Token);
                    try { await socket.SendAsync(Encoding.UTF8.GetBytes(body.ToJsonString()), WebSocketMessageType.Text, true, linked.Token); }
                    finally { sendLock.Release(); }
                }
                long cursor = 0;
                var pumping = Task.Run(async () =>
                {
                    try
                    {
                    while (!linked.IsCancellationRequested)
                    {
                        foreach (var ev in journal.Events(Interlocked.Read(ref cursor))) await Send(ev);
                        await controller.Tick();
                        await Send(new JsonObject { ["type"] = "heartbeat", ["paused"] = controller.Paused });
                        await Task.Delay(1500, linked.Token);
                    }
                    }
                    finally { linked.Cancel(); }
                }, linked.Token);
                try
                {
                    var buffer = new byte[65536];
                    while (socket.State == WebSocketState.Open)
                    {
                        using var message = new MemoryStream();
                        WebSocketReceiveResult part;
                        do
                        {
                            part = await socket.ReceiveAsync(buffer, linked.Token);
                            if (part.MessageType == WebSocketMessageType.Close) throw new IOException("Connection closed");
                            message.Write(buffer, 0, part.Count);
                            if (message.Length > 2_000_000) throw new IOException("Command too large");
                        } while (!part.EndOfMessage);
                        var body = JsonNode.Parse(message.ToArray())!.AsObject();
                        if (body["type"]?.GetValue<string>() == "ack")
                        { Interlocked.Exchange(ref cursor, body["seq"]!.GetValue<long>()); continue; }
                        if (body["type"]?.GetValue<string>() != "command") continue;
                        var owned = body.DeepClone().AsObject();
                        _ = Task.Run(async () =>
                        {
                            JsonNode result;
                            try { result = await controller.Execute(owned); }
                            catch (Exception ex) { result = new JsonObject { ["ok"] = false, ["error"] = ex.Message }; }
                            try { await Send(new JsonObject { ["type"] = "result", ["id"] = owned["id"]!.DeepClone(), ["result"] = result }); }
                            catch { /* Durable results are returned when commands are redelivered. */ }
                        }, CancellationToken.None);
                    }
                }
                finally
                {
                    linked.Cancel();
                    try { await pumping; } catch (OperationCanceledException) { } catch (WebSocketException) { }
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Status = "连接中断：" + ex.GetType().Name;
                await Task.Delay(TimeSpan.FromSeconds(backoff), stop);
                backoff = Math.Min(backoff * 2, 30);
            }
        }
    }
}
