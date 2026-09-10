using System.Text.Json;

namespace Erp.Infrastructure.AI;

/// 一個工具的宣告：名稱、給 LLM 看的說明、以及參數的 JSON Schema。
public sealed record ToolDefinition(
    string Name,
    string Description,
    IReadOnlyDictionary<string, JsonElement> Properties,
    IReadOnlyList<string> Required);
