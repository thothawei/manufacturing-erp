namespace Erp.Infrastructure.AI;

public sealed class AiAssistantOptions
{
    public const string SectionName = "AiAssistant";

    /// 未設定時由 SDK 讀取 ANTHROPIC_API_KEY 環境變數
    public string? ApiKey { get; set; }

    /// 覆寫 API 端點。留空就直接打 Anthropic 官方；測試假伺服器與 OmniRoute
    /// 這類相容 gateway 才需要設定（例如 http://localhost:20128）。
    /// 只填到根網址，後面的 /v1/messages 由 SDK 自己接上去。
    public string? BaseUrl { get; set; }

    /// server-side refusal fallback 是 Anthropic 官方端點才有的參數。
    /// 打 gateway 時要關掉：對方不認得 fallbacks，而備援本來就是 gateway
    /// 自己在做的事，兩層備援疊在一起只會讓「誰答的」變得無法追。
    public bool UseServerSideFallback { get; set; } = true;

    /// 打官方端點時是 Anthropic 的模型代號；打 OmniRoute 時是它的模型代號
    /// （auto 代表交給它自動選，也可以寫成 anthropic/claude-opus-5 指定）。
    public string Model { get; set; } = "claude-opus-5";

    public int MaxTokens { get; set; } = 8_000;

    /// tool-use 迴圈的輪數上限，防止 LLM 無止境地互相呼叫工具
    public int MaxToolIterations { get; set; } = 5;

    /// 單次 LLM 呼叫的逾時秒數。SDK 預設是 10 分鐘，對互動式查詢太長 ——
    /// 使用者會先放棄，但請求還掛在那裡。重試次數沿用 SDK 預設的 2 次。
    public int TimeoutSeconds { get; set; } = 60;
}
