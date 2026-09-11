using System.Net;
using System.Net.Http.Json;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using Microsoft.Extensions.Options;

namespace Erp.Infrastructure.Rag;

/// IEmbeddingClient 的本機 Ollama 實作。
///
/// 用 /api/embeddings（單筆、prompt 欄位）而不是較新的 /api/embed，
/// 因為它在所有 Ollama 版本上都存在，而本模組一次只需要一段文字的向量。
///
/// 所有失敗都轉成 EmbeddingUnavailableException：
/// 「這台機器沒裝 Ollama」是本模組最常見的情境，不該以未預期例外的形式
/// 穿過 tool-use 迴圈變成 HTTP 500。
public sealed class OllamaEmbeddingClient : IEmbeddingClient, IDisposable
{
    /// 請求體用同一組設定序列化：預設編碼器會把中文逃逸成 \uXXXX，
    /// 抓包或看 Ollama log 時讀不出來送了什麼
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    private readonly HttpClient _http;
    private readonly RagOptions _options;

    public OllamaEmbeddingClient(IOptions<RagOptions> options)
    {
        _options = options.Value;
        _http = new HttpClient
        {
            BaseAddress = new Uri(_options.OllamaBaseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds)
        };
    }

    public string ModelName => _options.EmbeddingModel;

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("要產生向量的文字不可為空", nameof(text));
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsJsonAsync(
                "api/embeddings", new EmbeddingRequest(_options.EmbeddingModel, text), JsonOptions, ct);
        }
        catch (HttpRequestException ex)
        {
            // 最常見的一條：本機沒裝 Ollama，或裝了但沒啟動
            throw new EmbeddingUnavailableException(
                $"無法連線到 Ollama（{_options.OllamaBaseUrl}）。"
                + "文件語意檢索需要本機的 Ollama 服務，請確認它已啟動。", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // HttpClient 逾時會以 TaskCanceledException 呈現；
            // 呼叫端主動取消時 ct 會是 cancelled，那種情況要原樣往上拋
            throw new EmbeddingUnavailableException(
                $"Ollama 在 {_options.TimeoutSeconds} 秒內沒有回應。", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            // 模型沒 pull 時 Ollama 回 404，訊息要寫明解法 ——
            // 「404」本身對使用者沒有任何行動指引
            throw new EmbeddingUnavailableException(
                response.StatusCode == HttpStatusCode.NotFound
                    ? $"Ollama 找不到模型 {_options.EmbeddingModel}，請先執行 ollama pull {_options.EmbeddingModel}。"
                    : $"Ollama 回應失敗（HTTP {(int)response.StatusCode}）。");
        }

        EmbeddingResponse? payload;
        try
        {
            payload = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(JsonOptions, ct);
        }
        catch (JsonException ex)
        {
            throw new EmbeddingUnavailableException("Ollama 的回應不是有效的 JSON。", ex);
        }

        if (payload?.Embedding is null || payload.Embedding.Length == 0)
        {
            // 回 200 但沒有向量。硬往下走會把一個空向量寫進索引，
            // 之後每次查詢都拿它算出 0 分 —— 靜默壞掉比直接報錯難追得多
            throw new EmbeddingUnavailableException(
                $"Ollama 回應中沒有向量資料（模型 {_options.EmbeddingModel}）。");
        }

        return payload.Embedding;
    }

    public void Dispose() => _http.Dispose();

    private sealed record EmbeddingRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("prompt")] string Prompt);

    private sealed record EmbeddingResponse(
        [property: JsonPropertyName("embedding")] float[]? Embedding);
}
