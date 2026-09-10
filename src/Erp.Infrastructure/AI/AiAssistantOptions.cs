namespace Erp.Infrastructure.AI;

public sealed class AiAssistantOptions
{
    public const string SectionName = "AiAssistant";

    /// 未設定時由 SDK 讀取 ANTHROPIC_API_KEY 環境變數
    public string? ApiKey { get; set; }

    /// 覆寫 API 端點。正式環境留空即可；測試與私有 gateway 才需要設定。
    public string? BaseUrl { get; set; }

    public string Model { get; set; } = "claude-opus-5";

    public int MaxTokens { get; set; } = 8_000;

    /// tool-use 迴圈的輪數上限，防止 LLM 無止境地互相呼叫工具
    public int MaxToolIterations { get; set; } = 5;

    /// 單次 LLM 呼叫的逾時秒數。SDK 預設是 10 分鐘，對互動式查詢太長 ——
    /// 使用者會先放棄，但請求還掛在那裡。重試次數沿用 SDK 預設的 2 次。
    public int TimeoutSeconds { get; set; } = 60;
}
