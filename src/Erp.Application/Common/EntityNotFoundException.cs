namespace Erp.Application.Common;

/// 查無資料。AI 工具層會把它轉成「查無此料件」的 tool_result，而不是讓 LLM 自行臆測。
public sealed class EntityNotFoundException(string entityName, string key)
    : Exception($"找不到{entityName}：{key}")
{
    public string EntityName { get; } = entityName;
    public string Key { get; } = key;
}
