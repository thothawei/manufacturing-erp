using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using Erp.Application.Common;
using Erp.Application.Inventory;
using Erp.Application.Items;

namespace Erp.Infrastructure.AI;

public sealed record ToolExecutionResult(string Content, bool IsError);

/// 把 LLM 的工具呼叫路由到既有的 Application Service。
///
/// 這一層刻意很薄：不做任何計算，只負責取參數、呼叫服務、把結果序列化成 JSON。
/// 所有數字都由 Application 層算好，LLM 拿到的永遠是後端的計算結果。
public sealed class ToolDispatcher(
    ItemMasterQueryService itemMasterQueryService,
    InventoryQueryService inventoryQueryService)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },

        // 預設編碼器會把中文逃逸成 \uXXXX，一個字變六個字元，
        // 送進 LLM 就是六倍的 token 成本。放行 Unicode，但仍逃脫 HTML 敏感字元。
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        WriteIndented = false
    };

    public async Task<ToolExecutionResult> ExecuteAsync(
        string toolName, JsonElement arguments, CancellationToken ct = default)
    {
        try
        {
            return toolName switch
            {
                ToolCatalog.SearchItems => Ok(await itemMasterQueryService.SearchByKeywordAsync(
                    RequireString(arguments, "keyword"), ct)),

                ToolCatalog.GetItemInventoryStatus => Ok(await inventoryQueryService.GetStockAsync(
                    RequireString(arguments, "item_code"), ct)),

                _ => Error($"未知的工具名稱：{toolName}")
            };
        }
        catch (EntityNotFoundException ex)
        {
            // 查無資料是正常結果，不是系統故障。如實回報讓 LLM 告訴使用者查不到，
            // 而不是讓它自己編一個看起來合理的答案。
            return Error(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return Error($"參數錯誤：{ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            return Error(ex.Message);
        }
    }

    private static ToolExecutionResult Ok<T>(T value)
        => new(JsonSerializer.Serialize(value, JsonOptions), IsError: false);

    private static ToolExecutionResult Error(string message)
        => new(JsonSerializer.Serialize(new { error = message }, JsonOptions), IsError: true);

    private static string RequireString(JsonElement arguments, string propertyName)
    {
        if (arguments.ValueKind != JsonValueKind.Object
            || !arguments.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException($"缺少必要參數 {propertyName}");
        }

        var value = property.GetString();
        return string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"參數 {propertyName} 不可為空")
            : value;
    }
}
