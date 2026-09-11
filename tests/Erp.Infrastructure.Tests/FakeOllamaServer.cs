using System.Net;
using System.Text;

namespace Erp.Infrastructure.Tests;

/// 假的 Ollama 伺服器。
/// 用來檢查 OllamaEmbeddingClient 實際送出的請求長什麼樣子，以及各種失敗回應的處理 ——
/// 這台開發機沒有裝 Ollama，而「型別轉換編譯得過」不代表 wire format 正確。
public sealed class FakeOllamaServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Task _loop;

    public FakeOllamaServer()
    {
        var port = GetFreePort();
        BaseUrl = $"http://localhost:{port}";
        _listener.Prefixes.Add($"{BaseUrl}/");
        _listener.Start();

        _loop = Task.Run(HandleRequestsAsync);
    }

    public string BaseUrl { get; }

    public int StatusCode { get; set; } = 200;

    /// 回應體。預設回一個 4 維向量
    public string ResponseBody { get; set; } = "{\"embedding\":[0.1,0.2,0.3,0.4]}";

    /// 回應前的延遲，用來測試逾時
    public TimeSpan ResponseDelay { get; set; } = TimeSpan.Zero;

    public List<string> ReceivedBodies { get; } = [];
    public List<string> ReceivedPaths { get; } = [];
    public List<string> ReceivedMethods { get; } = [];

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
            ReceivedPaths.Add(context.Request.Url?.AbsolutePath ?? "");
            ReceivedMethods.Add(context.Request.HttpMethod);

            if (ResponseDelay > TimeSpan.Zero)
            {
                await Task.Delay(ResponseDelay);
            }

            try
            {
                var bytes = Encoding.UTF8.GetBytes(ResponseBody);
                context.Response.StatusCode = StatusCode;
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            }
            catch (Exception)
            {
                // 用戶端已因逾時放棄，回應物件這時可能已被釋放 ——
                // 連設定 ContentLength64 都會擲例外。逾時測試本來就會走到這裡，
                // 不該讓它在 Dispose 等待迴圈時變成測試失敗
                return;
            }
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

    /// 找一個沒人在聽的 port，用來模擬「本機沒裝 Ollama」
    public static string UnusedBaseUrl()
    {
        var port = GetFreePort();
        return $"http://localhost:{port}";
    }
}
