namespace Erp.Application.Abstractions;

/// AI 助理的 port。定義在 Application 層，實作在 Infrastructure.AI，
/// 讓 Application 與 Domain 完全不需要知道 LLM 的存在。
public interface IAiAssistantService
{
    Task<string> AskAsync(string question, CancellationToken ct = default);
}
