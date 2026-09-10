using System.Net;
using System.Text;

namespace Erp.Infrastructure.Tests;

/// 假的 Anthropic API 伺服器。
/// 用來檢查 AnthropicLlmClient 實際送出的 HTTP 請求長什麼樣子 ——
/// 型別轉換編譯得過不代表 wire format 正確，而真打 API 要金鑰也要花錢。
public sealed class FakeAnthropicServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Queue<string> _responseBodies;
    private readonly Task _loop;

    public FakeAnthropicServer(params string[] responseBodies)
    {
        _responseBodies = new Queue<string>(responseBodies);

        var port = GetFreePort();
        BaseUrl = $"http://localhost:{port}";
        _listener.Prefixes.Add($"{BaseUrl}/");
        _listener.Start();

        _loop = Task.Run(HandleRequestsAsync);
    }

    public string BaseUrl { get; }

    public List<string> ReceivedBodies { get; } = [];
    public List<string> ReceivedBetaHeaders { get; } = [];
    public List<string> ReceivedPaths { get; } = [];

    private async Task HandleRequestsAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception)
            {
                return; // 伺服器關閉
            }

            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            ReceivedBodies.Add(await reader.ReadToEndAsync());
            ReceivedBetaHeaders.Add(context.Request.Headers["anthropic-beta"] ?? "");
            ReceivedPaths.Add(context.Request.Url?.AbsolutePath ?? "");

            var body = _responseBodies.Count > 0
                ? _responseBodies.Dequeue()
                : throw new InvalidOperationException("假伺服器的回應腳本已用完");

            var bytes = Encoding.UTF8.GetBytes(body);
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }
    }

    private static int GetFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose()
    {
        _listener.Stop();
        _listener.Close();
        _loop.Wait(TimeSpan.FromSeconds(2));
    }

    public static string TextResponse(string text)
    {
        var escaped = System.Text.Json.JsonSerializer.Serialize(text);
        return "{\"id\":\"msg_test\",\"type\":\"message\",\"role\":\"assistant\","
             + "\"model\":\"claude-opus-5\","
             + "\"content\":[{\"type\":\"text\",\"text\":" + escaped + "}],"
             + "\"stop_reason\":\"end_turn\",\"stop_sequence\":null,"
             + "\"usage\":{\"input_tokens\":10,\"output_tokens\":5}}";
    }

    public static string ToolUseResponse(string toolUseId, string toolName, string inputJson)
    {
        return "{\"id\":\"msg_test\",\"type\":\"message\",\"role\":\"assistant\","
             + "\"model\":\"claude-opus-5\","
             + "\"content\":[{\"type\":\"tool_use\",\"id\":\"" + toolUseId + "\","
             + "\"name\":\"" + toolName + "\",\"input\":" + inputJson + "}],"
             + "\"stop_reason\":\"tool_use\",\"stop_sequence\":null,"
             + "\"usage\":{\"input_tokens\":10,\"output_tokens\":5}}";
    }
}
