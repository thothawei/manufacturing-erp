namespace Erp.Application.Abstractions;

/// 助理的回答，外加這次對話的識別碼。
///
/// ConversationId 由伺服器產生並回傳，呼叫端下一次帶著它就能接續同一段對話。
/// 刻意不讓呼叫端自己挑 id：那樣任何人猜到別人的 id 就能讀到別人的對話歷史。
public sealed record AiAnswer(string Answer, string ConversationId);

/// AI 助理的 port。定義在 Application 層，實作在 Infrastructure.AI，
/// 讓 Application 與 Domain 完全不需要知道 LLM 的存在。
public interface IAiAssistantService
{
    /// conversationId 為 null（或找不到）時開始一段新對話。
    /// role 為 null 時不限角色；不認得的角色名稱會擲 ArgumentException。
    Task<AiAnswer> AskAsync(
        string question, string? conversationId = null, string? role = null,
        CancellationToken ct = default);
}
