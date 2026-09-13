using Erp.Application.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Erp.Infrastructure.AI;

/// tool-use 迴圈：把問題送給 LLM，LLM 要求呼叫工具就執行並把結果送回，
/// 直到它回覆純文字為止，並設輪數上限防止失控。
///
/// 跨請求的對話記憶由 IConversationStore 提供：前幾輪的問答會以純文字訊息
/// 接在這次的問題之前，讓「那 CABLE-07 呢」這種追問有上下文可循。
public sealed class AiAssistantService(
    ILlmClient llmClient,
    ToolDispatcher toolDispatcher,
    IConversationStore conversationStore,
    IOptions<AiAssistantOptions> options,
    ILogger<AiAssistantService> logger) : IAiAssistantService
{
    private readonly AiAssistantOptions _options = options.Value;

    public async Task<AiAnswer> AskAsync(
        string question, string? conversationId = null, string? role = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            throw new ArgumentException("問題不可為空", nameof(question));
        }

        // 先擋下不認得的角色再做任何事：打錯字的角色名如果靜默變成「不限角色」，
        // 這一層就等於不存在
        AssistantScope.EnsureKnown(role);

        var trimmedQuestion = question.Trim();

        // 沒帶 id、或帶了一個已經過期／被淘汰的 id，都當成新對話。
        // 後者不報錯是刻意的：使用者能做的也只有重新開始，回一個錯誤只是多一個步驟。
        var id = string.IsNullOrWhiteSpace(conversationId)
            ? Guid.NewGuid().ToString()
            : conversationId;

        var messages = new List<LlmMessage>();

        // 對話記憶以「角色 + 識別碼」為鍵。共用一個鍵的話，
        // 拿著品保的識別碼改用採購角色再問一次，就讀得到品保那一段歷史 ——
        // 工具過濾擋住的東西會從歷史繞回來。
        var storeKey = $"{role ?? "-"}:{id}";

        foreach (var turn in conversationStore.GetRecentTurns(storeKey))
        {
            messages.Add(LlmMessage.User(turn.Question));
            messages.Add(new LlmMessage(LlmRole.Assistant, [new LlmTextBlock(turn.Answer)]));
        }

        messages.Add(LlmMessage.User(trimmedQuestion));

        for (var iteration = 1; iteration <= _options.MaxToolIterations; iteration++)
        {
            // 傳快照而不是 messages 本身：這個 List 在迴圈後續還會被 Add，
            // 直接傳參考的話，任何暫存請求的實作（重試、記錄、批次）
            // 事後讀到的都會是被改過的內容
            var response = await llmClient.SendAsync(
                new LlmRequest(AiSystemPrompt.Text, [.. messages], AssistantScope.ToolsFor(role)), ct);

            if (!response.RequiresToolExecution)
            {
                // 只有問答文字進歷史，工具往返不進 —— 理由寫在 ConversationTurn 上
                conversationStore.Append(storeKey, new ConversationTurn(trimmedQuestion, response.Text));
                return new AiAnswer(response.Text, id);
            }

            // LLM 可能一次要求多個工具。所有結果必須放進「同一則」使用者訊息回覆，
            // 拆成多則會讓後續回合不再平行呼叫工具。
            var toolResults = new List<LlmContentBlock>();
            foreach (var toolUse in response.ToolUses)
            {
                var result = await toolDispatcher.ExecuteAsync(
                    toolUse.ToolName, toolUse.Arguments, role, ct);

                logger.LogInformation(
                    "第 {Iteration} 輪呼叫工具 {ToolName}，是否錯誤：{IsError}",
                    iteration, toolUse.ToolName, result.IsError);

                toolResults.Add(new LlmToolResultBlock(toolUse.ToolUseId, result.Content, result.IsError));
            }

            messages.Add(new LlmMessage(LlmRole.Assistant, response.Content));
            messages.Add(new LlmMessage(LlmRole.User, toolResults));
        }

        logger.LogWarning("tool-use 迴圈達到 {Max} 輪上限仍未得到結論", _options.MaxToolIterations);

        // 回傳訊息而不是拋例外：使用者需要知道發生什麼事，而不是看到一個 500。
        // 這一輪刻意不寫進歷史：它沒有結論，留著只會讓下一輪帶著一段沒有資訊的對白。
        return new AiAnswer(
            $"查詢過程需要的步驟超過上限（{_options.MaxToolIterations} 輪）仍未完成，"
            + "請把問題拆得更具體一些，或直接指定料號與工單號。", id);
    }
}
