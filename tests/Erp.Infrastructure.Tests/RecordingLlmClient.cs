using System.Text;
using System.Text.Json;
using Erp.Infrastructure.AI;

namespace Erp.Infrastructure.Tests;

/// 包在真 ILlmClient 外面，把每一輪的請求與回應留下來，
/// 好讓真實 API 驗證能產出一份可歸檔的純文字紀錄（而不是跑完就沒了）。
internal sealed class RecordingLlmClient(ILlmClient inner) : ILlmClient
{
    private readonly List<(LlmRequest Request, LlmResponse Response)> _rounds = [];

    public IReadOnlyList<(LlmRequest Request, LlmResponse Response)> Rounds => _rounds;

    /// 這一整回合裡 LLM 要求呼叫過的工具（依序，可能重複）
    public IReadOnlyList<LlmToolUseBlock> ToolUses =>
        [.. _rounds.SelectMany(r => r.Response.ToolUses)];

    public async Task<LlmResponse> SendAsync(LlmRequest request, CancellationToken ct = default)
    {
        var response = await inner.SendAsync(request, ct);
        _rounds.Add((request, response));
        return response;
    }

    /// 把整段對話攤成純文字。system prompt 只記長度 —— 它每輪都一樣且很長，
    /// 全文重複四遍只會讓紀錄難讀。
    public string ToTranscript(string question, string answer)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"問題：{question}");
        sb.AppendLine($"輪數：{_rounds.Count}");
        sb.AppendLine($"system prompt 長度：{_rounds[0].Request.SystemPrompt.Length} 字元");
        sb.AppendLine($"送出的工具定義：{_rounds[0].Request.Tools.Count} 個");
        sb.AppendLine();

        for (var i = 0; i < _rounds.Count; i++)
        {
            var (request, response) = _rounds[i];
            sb.AppendLine($"## 第 {i + 1} 輪");
            sb.AppendLine();
            sb.AppendLine("### 送出的訊息");

            foreach (var message in request.Messages)
            {
                foreach (var block in message.Content)
                {
                    sb.AppendLine($"- [{message.Role}] {Describe(block)}");
                }
            }

            sb.AppendLine();
            sb.AppendLine("### 收到的回應");

            foreach (var block in response.Content)
            {
                sb.AppendLine($"- {Describe(block)}");
            }

            sb.AppendLine();
        }

        sb.AppendLine("## 最終回答");
        sb.AppendLine();
        sb.AppendLine(answer);

        return Redact(sb.ToString());
    }

    private static string Describe(LlmContentBlock block) => block switch
    {
        LlmTextBlock text => $"text: {text.Text}",
        LlmToolUseBlock toolUse =>
            $"tool_use: {toolUse.ToolName} 參數 {Compact(toolUse.Arguments)}（id={toolUse.ToolUseId}）",
        LlmToolResultBlock result =>
            $"tool_result: id={result.ToolUseId} is_error={result.IsError}\n\n```json\n{result.Content}\n```\n",
        _ => block.ToString() ?? ""
    };

    private static string Compact(JsonElement element)
        => JsonSerializer.Serialize(element, new JsonSerializerOptions
        {
            // 參數裡可能有中文料號/關鍵字，不要被逸成 \uXXXX
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });

    /// 防呆：紀錄理論上碰不到金鑰（中性模型裡本來就沒有），
    /// 但這份檔案會被 commit，多一道遮蔽比事後補救便宜。
    private static string Redact(string text)
        => System.Text.RegularExpressions.Regex.Replace(text, @"sk-ant-[A-Za-z0-9_\-]+", "sk-ant-***");
}
