using Erp.Application.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Erp.Infrastructure.AI;

/// tool-use 迴圈：把問題送給 LLM，LLM 要求呼叫工具就執行並把結果送回，
/// 直到它回覆純文字為止，並設輪數上限防止失控。
public sealed class AiAssistantService(
    ILlmClient llmClient,
    ToolDispatcher toolDispatcher,
    IOptions<AiAssistantOptions> options,
    ILogger<AiAssistantService> logger) : IAiAssistantService
{
    private readonly AiAssistantOptions _options = options.Value;

    public async Task<string> AskAsync(string question, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            throw new ArgumentException("問題不可為空", nameof(question));
        }

        var messages = new List<LlmMessage> { LlmMessage.User(question.Trim()) };

        for (var iteration = 1; iteration <= _options.MaxToolIterations; iteration++)
        {
            var response = await llmClient.SendAsync(
                new LlmRequest(AiSystemPrompt.Text, messages, ToolCatalog.All), ct);

            if (!response.RequiresToolExecution)
            {
                return response.Text;
            }

            // LLM 可能一次要求多個工具。所有結果必須放進「同一則」使用者訊息回覆，
            // 拆成多則會讓後續回合不再平行呼叫工具。
            var toolResults = new List<LlmContentBlock>();
            foreach (var toolUse in response.ToolUses)
            {
                var result = await toolDispatcher.ExecuteAsync(toolUse.ToolName, toolUse.Arguments, ct);

                logger.LogInformation(
                    "第 {Iteration} 輪呼叫工具 {ToolName}，是否錯誤：{IsError}",
                    iteration, toolUse.ToolName, result.IsError);

                toolResults.Add(new LlmToolResultBlock(toolUse.ToolUseId, result.Content, result.IsError));
            }

            messages.Add(new LlmMessage(LlmRole.Assistant, response.Content));
            messages.Add(new LlmMessage(LlmRole.User, toolResults));
        }

        logger.LogWarning("tool-use 迴圈達到 {Max} 輪上限仍未得到結論", _options.MaxToolIterations);

        // 回傳訊息而不是拋例外：使用者需要知道發生什麼事，而不是看到一個 500
        return $"查詢過程需要的步驟超過上限（{_options.MaxToolIterations} 輪）仍未完成，"
             + "請把問題拆得更具體一些，或直接指定料號與工單號。";
    }
}
