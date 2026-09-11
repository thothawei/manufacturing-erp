using Erp.Infrastructure.Rag;

namespace Erp.Infrastructure.Tests;

/// 探測本機有沒有可用的 Ollama 與指定模型。
///
/// 為什麼需要這個：檢索品質只有接真模型才測得出來，但 CI 上沒有 Ollama。
/// 硬跑會讓 CI 永遠紅，而用假 embedding 又等於沒測（那正是第一版選錯模型的原因）。
/// 折衷是「有就跑、沒有就標記 skip」—— 並在 README 寫明它不在 CI 的把關範圍內。
internal static class OllamaAvailability
{
    /// 設了這個環境變數就不准 skip。
    ///
    /// 沒有它的話，CI 上的 Ollama 安裝一旦悄悄失敗，整組測試會變成 skip 而 job 照樣綠 ——
    /// 那就是一條假防線：看起來有在把關，實際上什麼都沒跑。
    /// CI 會設這個變數，所以「環境壞了」會以測試失敗的形式現形，而不是靜靜消失。
    private const string RequireVariable = "RAG_REQUIRE_OLLAMA";

    private static readonly Lazy<string?> Unavailable = new(Probe);

    /// null 代表可用（或被要求不准 skip）；否則是 skip 的理由
    public static string? Reason => Unavailable.Value;

    private static string? Probe()
    {
        var model = new RagOptions().EmbeddingModel;
        var skipForbidden = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(RequireVariable));

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var tags = http.GetStringAsync(new RagOptions().OllamaBaseUrl.TrimEnd('/') + "/api/tags")
                .GetAwaiter().GetResult();

            if (tags.Contains(model, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return skipForbidden
                ? null   // 讓測試跑下去並失敗，不要 skip
                : $"本機 Ollama 沒有模型 {model}，請執行 ollama pull {model}";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return skipForbidden
                ? null
                : "本機沒有執行中的 Ollama，檢索品質測試略過（其餘測試不受影響）";
        }
    }
}

/// 只在本機有 Ollama 時才執行的 Fact。
/// 探測是同步的，結果由 Lazy 快取，整個測試回合只打一次 /api/tags。
public sealed class OllamaFactAttribute : FactAttribute
{
    public OllamaFactAttribute() => Skip = OllamaAvailability.Reason;
}

public sealed class OllamaTheoryAttribute : TheoryAttribute
{
    public OllamaTheoryAttribute() => Skip = OllamaAvailability.Reason;
}
