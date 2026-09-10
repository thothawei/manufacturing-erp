using System.Text.Json;

namespace Erp.Infrastructure.AI;

public enum LlmRole
{
    User,
    Assistant
}

/// 供應商中性的內容區塊。
/// 刻意不直接用 Anthropic SDK 的型別 —— 換成別家 LLM 時只要換 ILlmClient 的實作，
/// tool-use 迴圈、ToolCatalog、ToolDispatcher 都不必動。
public abstract record LlmContentBlock;

public sealed record LlmTextBlock(string Text) : LlmContentBlock;

public sealed record LlmToolUseBlock(string ToolUseId, string ToolName, JsonElement Arguments) : LlmContentBlock;

public sealed record LlmToolResultBlock(string ToolUseId, string Content, bool IsError) : LlmContentBlock;

public sealed record LlmMessage(LlmRole Role, IReadOnlyList<LlmContentBlock> Content)
{
    public static LlmMessage User(string text) => new(LlmRole.User, [new LlmTextBlock(text)]);
}

public sealed record LlmRequest(
    string SystemPrompt,
    IReadOnlyList<LlmMessage> Messages,
    IReadOnlyList<ToolDefinition> Tools);

public sealed record LlmResponse(IReadOnlyList<LlmContentBlock> Content)
{
    public IReadOnlyList<LlmToolUseBlock> ToolUses => [.. Content.OfType<LlmToolUseBlock>()];

    public bool RequiresToolExecution => ToolUses.Count > 0;

    public string Text => string.Join("\n", Content.OfType<LlmTextBlock>().Select(b => b.Text)).Trim();
}

/// LLM 供應商的抽象。實作只負責型別轉換與 HTTP，不含任何業務邏輯。
public interface ILlmClient
{
    Task<LlmResponse> SendAsync(LlmRequest request, CancellationToken ct = default);
}
