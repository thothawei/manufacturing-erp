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

    /// 對話記憶保留的輪數上限。截斷由 IConversationStore 的實作負責 ——
    /// AiAssistantService 不重複做一次，同一條規則有兩個執行點就會有兩個真相。
    ///
    /// 六輪是刻意的折衷：追問指的幾乎都是剛剛講過的事，而每多留一輪，
    /// 之後每一次呼叫都要重送一次那段文字 —— 成本是按輪數線性累加的。
    public int MaxConversationTurns { get; set; } = 6;

    /// 同時保留的對話數上限。超過時淘汰最久沒被碰過的那個。
    public int MaxConversations { get; set; } = 200;

    /// 對話閒置多久之後丟棄
    public int ConversationIdleMinutes { get; set; } = 60;

    /// 單次 LLM 呼叫的逾時秒數。SDK 預設是 10 分鐘，對互動式查詢太長 ——
    /// 使用者會先放棄，但請求還掛在那裡。重試次數沿用 SDK 預設的 2 次。
    public int TimeoutSeconds { get; set; } = 60;
}
