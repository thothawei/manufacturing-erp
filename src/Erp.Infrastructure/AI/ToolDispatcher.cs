using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using System.Globalization;
using Erp.Application.Bom;
using Erp.Application.Common;
using Erp.Application.Inventory;
using Erp.Application.Items;
using Erp.Application.Mrp;
using Erp.Application.Production;
using Erp.Application.Purchasing;
using Erp.Application.Quality;
using Erp.Infrastructure.Json;

namespace Erp.Infrastructure.AI;

public sealed record ToolExecutionResult(string Content, bool IsError);

/// 把 LLM 的工具呼叫路由到既有的 Application Service。
///
/// 這一層刻意很薄：不做任何計算，只負責取參數、呼叫服務、把結果序列化成 JSON。
/// 所有數字都由 Application 層算好，LLM 拿到的永遠是後端的計算結果。
public sealed class ToolDispatcher(
    ItemMasterQueryService itemMasterQueryService,
    InventoryQueryService inventoryQueryService,
    BomExplosionService bomExplosionService,
    WorkOrderProgressService workOrderProgressService,
    WorkOrderRiskService workOrderRiskService,
    MrpCalculationService mrpCalculationService,
    PurchasingQueryService purchasingQueryService,
    QualityInspectionQueryService qualityInspectionQueryService)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters =
        {
            new System.Text.Json.Serialization.JsonStringEnumConverter(),
            new NormalizedDecimalConverter()
        },

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

                ToolCatalog.CheckMaterialSufficiency => Ok(await bomExplosionService.CalculateMaxBuildableAsync(
                    RequireString(arguments, "item_code"),
                    OptionalDecimal(arguments, "planned_qty"), ct)),

                ToolCatalog.GetWorkOrderProgress => Ok(await workOrderProgressService.GetProgressAsync(
                    RequireString(arguments, "work_order_no"), ct)),

                ToolCatalog.ListWorkOrdersAtRisk => Ok(await workOrderRiskService.GetAtRiskWorkOrdersAsync(
                    OptionalDate(arguments, "date_range_start"),
                    OptionalDate(arguments, "date_range_end"), ct)),

                ToolCatalog.RunMrpShortageAnalysis => Ok(await mrpCalculationService.RunShortageAnalysisAsync(
                    OptionalInt(arguments, "planning_horizon_days"),
                    OptionalString(arguments, "item_code"), ct)),

                ToolCatalog.ListOpenPurchaseOrders => Ok(await purchasingQueryService.GetOpenPurchaseOrdersAsync(
                    OptionalString(arguments, "supplier_code"),
                    OptionalString(arguments, "item_code"), ct)),

                ToolCatalog.GetQualityInspectionSummary => Ok(await qualityInspectionQueryService.GetSummaryAsync(
                    OptionalString(arguments, "item_code"),
                    OptionalString(arguments, "work_order_no"),
                    OptionalDate(arguments, "date_range_start"),
                    OptionalDate(arguments, "date_range_end"), ct)),

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
            // ArgumentOutOfRangeException 也走這條（它繼承自 ArgumentException）
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
        if (!TryGetProperty(arguments, propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException($"缺少必要參數 {propertyName}");
        }

        var value = property.GetString();
        return string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"參數 {propertyName} 不可為空")
            : value;
    }

    private static string? OptionalString(JsonElement arguments, string propertyName)
    {
        if (!TryGetProperty(arguments, propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = property.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// LLM 有時會把數字放進字串（"100" 而不是 100），兩種都接受，
    /// 但不是合法數字時要明確報錯，不能默默當成沒給
    private static decimal? OptionalDecimal(JsonElement arguments, string propertyName)
    {
        if (!TryGetProperty(arguments, propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number => property.GetDecimal(),
            JsonValueKind.String => decimal.TryParse(
                property.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : throw new ArgumentException($"參數 {propertyName} 不是有效的數字：{property.GetString()}"),
            JsonValueKind.Null => null,
            _ => throw new ArgumentException($"參數 {propertyName} 必須是數字")
        };
    }

    private static int? OptionalInt(JsonElement arguments, string propertyName)
    {
        var value = OptionalDecimal(arguments, propertyName);
        if (value is null)
        {
            return null;
        }

        if (value != decimal.Truncate(value.Value))
        {
            throw new ArgumentException($"參數 {propertyName} 必須是整數：{value}");
        }

        return (int)value.Value;
    }

    private static DateOnly? OptionalDate(JsonElement arguments, string propertyName)
    {
        var text = OptionalString(arguments, propertyName);
        if (text is null)
        {
            return null;
        }

        return DateOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : throw new ArgumentException($"參數 {propertyName} 不是有效的日期，格式應為 YYYY-MM-DD：{text}");
    }

    private static bool TryGetProperty(JsonElement arguments, string propertyName, out JsonElement property)
    {
        property = default;
        return arguments.ValueKind == JsonValueKind.Object
            && arguments.TryGetProperty(propertyName, out property);
    }
}
