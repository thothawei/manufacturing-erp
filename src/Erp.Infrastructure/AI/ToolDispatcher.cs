using System.Diagnostics;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using Erp.Application.Bom;
using Erp.Application.Common;
using Erp.Application.Inventory;
using Erp.Application.Items;
using Erp.Application.Mrp;
using Erp.Application.Production;
using Erp.Application.Purchasing;
using Erp.Application.Quality;
using Erp.Infrastructure.Json;
using Microsoft.Extensions.Logging;

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
    QualityInspectionQueryService qualityInspectionQueryService,
    ILogger<ToolDispatcher> logger)
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
        // 每次工具呼叫都留一筆結構化紀錄：問了什麼、花多久、成不成功。
        // 這條 log 是 AI 助理唯一的稽核軌跡 —— 沒有它就只能看到最後那段自然語言，
        // 無從得知答案是根據哪些查詢組出來的。
        var stopwatch = Stopwatch.StartNew();
        var result = await ExecuteCoreAsync(toolName, arguments, ct);
        stopwatch.Stop();

        logger.LogInformation(
            "工具呼叫 {ToolName} 完成，成功：{Succeeded}，耗時 {ElapsedMs} ms，參數：{Arguments}",
            toolName, !result.IsError, stopwatch.ElapsedMilliseconds, Describe(arguments));

        return result;
    }

    private async Task<ToolExecutionResult> ExecuteCoreAsync(
        string toolName, JsonElement arguments, CancellationToken ct)
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

                _ => Error(ToolErrorCode.UnknownTool, $"未知的工具名稱：{toolName}")
            };
        }
        catch (EntityNotFoundException ex)
        {
            // 查無資料是正常結果，不是系統故障。如實回報讓 LLM 告訴使用者查不到，
            // 而不是讓它自己編一個看起來合理的答案。
            return Error(ToolErrorCode.EntityNotFound, ex.Message);
        }
        catch (ArgumentException ex)
        {
            // ArgumentOutOfRangeException 也走這條（它繼承自 ArgumentException）
            return Error(ToolErrorCode.InvalidArgument, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            // 參數本身合法，但這筆資料不適用這個問法（例如對原物料問可製造量）
            return Error(ToolErrorCode.NotApplicable, ex.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 呼叫端主動取消（使用者關掉連線、請求逾時）不是工具故障，原樣往上拋
            throw;
        }
        catch (Exception ex)
        {
            // 未預期的例外（資料庫連線失效、序列化失敗等）如果放它往上竄，
            // 會穿過 tool-use 迴圈變成 HTTP 500，整段對話跟著陣亡 ——
            // 即使其他工具的結果其實是好的。
            // 這裡把它降級成單一工具的失敗，讓 LLM 據實回報這項查不到。
            //
            // 例外全文只進伺服器 log，回給 LLM 的訊息不含型別、堆疊或任何內部細節：
            // 那些對 LLM 沒有用，還可能被它轉述給使用者。
            logger.LogError(ex,
                "工具 {ToolName} 執行時發生未預期的例外，參數：{Arguments}",
                toolName, Describe(arguments));

            return Error(ToolErrorCode.InternalError, "查詢時發生系統錯誤，這項資料目前查不到。");
        }
    }

    /// 參數要用同一組序列化設定輸出，JsonElement.ToString() 會把中文逃逸成
    /// \uXXXX，log 是給人看的，那樣根本讀不出來是查了什麼
    private static string Describe(JsonElement arguments)
        => JsonSerializer.Serialize(arguments, JsonOptions);

    private static ToolExecutionResult Ok<T>(T value)
        => new(JsonSerializer.Serialize(value, JsonOptions), IsError: false);

    private static ToolExecutionResult Error(ToolErrorCode code, string message)
        => new(
            JsonSerializer.Serialize(
                new { error_code = code.ToWireValue(), message }, JsonOptions),
            IsError: true);

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
