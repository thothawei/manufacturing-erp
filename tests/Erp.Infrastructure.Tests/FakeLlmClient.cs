using System.Text.Json;
using Erp.Infrastructure.AI;

namespace Erp.Infrastructure.Tests;

/// 依序吐出預先安排好的回應，並記錄每次收到的請求，
/// 讓測試能檢查「送回 LLM 的訊息長什麼樣子」。
public sealed class FakeLlmClient(params LlmResponse[] scriptedResponses) : ILlmClient
{
    private readonly Queue<LlmResponse> _responses = new(scriptedResponses);

    public List<LlmRequest> ReceivedRequests { get; } = [];

    /// 腳本用完後固定回傳的內容；設為 tool_use 可模擬「LLM 一直要求呼叫工具」
    public LlmResponse? RepeatingResponse { get; set; }

    public Task<LlmResponse> SendAsync(LlmRequest request, CancellationToken ct = default)
    {
        ReceivedRequests.Add(request);

        if (_responses.Count > 0)
        {
            return Task.FromResult(_responses.Dequeue());
        }

        return RepeatingResponse is not null
            ? Task.FromResult(RepeatingResponse)
            : throw new InvalidOperationException("FakeLlmClient 的腳本已用完，但服務仍在呼叫");
    }

    public static LlmResponse Text(string text) => new([new LlmTextBlock(text)]);

    public static LlmResponse ToolUse(string toolUseId, string toolName, object arguments)
        => new([new LlmToolUseBlock(toolUseId, toolName, JsonSerializer.SerializeToElement(arguments))]);

    public static LlmResponse ToolUses(params (string Id, string Name, object Arguments)[] calls)
        => new([.. calls.Select(c => new LlmToolUseBlock(
            c.Id, c.Name, JsonSerializer.SerializeToElement(c.Arguments)))]);
}
