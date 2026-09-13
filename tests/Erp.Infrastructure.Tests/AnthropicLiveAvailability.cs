using Microsoft.Extensions.Configuration;

namespace Erp.Infrastructure.Tests;

/// 解析「要不要真的打 api.anthropic.com」以及金鑰從哪裡來。
///
/// 為什麼需要這個：wire format 一直是用 FakeAnthropicServer 驗的 ——
/// 那驗得了「我們送出的 JSON 長什麼樣」，驗不了「真實端點收不收」。
/// 工具的 input schema 不合法、beta 標頭被拒、fallback 參數改名，
/// 假伺服器一律照單全收，全綠，而正式環境第一次呼叫就 400。
///
/// 但這組測試會花錢、需要網路，不能無條件跑，所以預設「沒金鑰就 skip」。
internal static class AnthropicLiveAvailability
{
    /// 設了這個環境變數就不准 skip。
    ///
    /// 理由跟 RAG_REQUIRE_OLLAMA 一樣：沒有它的話，金鑰忘了設或讀取路徑壞掉，
    /// 整組會安靜地變成 skip 而回合照樣綠 —— 那是一條假防線。
    private const string RequireVariable = "ANTHROPIC_REQUIRE_LIVE";

    /// 金鑰寫在 Erp.Api 的 user-secrets（scripts/set-api-key.sh 就是寫這裡）。
    /// 測試專案共用同一個 id，才不必為了跑測試再存一份金鑰。
    private const string ApiUserSecretsId = "f31d2f5c-710d-4eff-9044-2b97308372b8";

    private static readonly Lazy<(string? Key, string? Reason)> Resolved = new(Probe);

    /// null 代表可以跑；否則是 skip 的理由
    public static string? Reason => Resolved.Value.Reason;

    /// 只有 Reason 是 null 時才保證非空
    public static string ApiKey =>
        Resolved.Value.Key ?? throw new InvalidOperationException("沒有可用的 Anthropic 金鑰");

    private static (string?, string?) Probe()
    {
        var skipForbidden = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(RequireVariable));

        // 環境變數優先：CI 用 secret 注入時走這條，本機開發走 user-secrets
        var key = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");

        if (string.IsNullOrWhiteSpace(key))
        {
            key = new ConfigurationBuilder()
                .AddUserSecrets(ApiUserSecretsId)
                .Build()["AiAssistant:ApiKey"];
        }

        if (!string.IsNullOrWhiteSpace(key))
        {
            return (key.Trim(), null);
        }

        return skipForbidden
            // 不准 skip 時回傳一個必定被拒絕的金鑰：測試會以「失敗」現形，
            // 而不是安靜地消失在 skip 清單裡
            ? ("missing-api-key", null)
            : ("", "沒有 Anthropic 金鑰，真實 API 驗證略過（執行 ./scripts/set-api-key.sh 後即可跑）");
    }
}

/// 只在有金鑰時才執行的 Fact。金鑰解析結果由 Lazy 快取。
public sealed class AnthropicLiveFactAttribute : FactAttribute
{
    public AnthropicLiveFactAttribute() => Skip = AnthropicLiveAvailability.Reason;
}
