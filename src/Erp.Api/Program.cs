using Erp.Api.ErrorHandling;
using Erp.Application;
using Erp.Application.Abstractions;
using Erp.Application.Bom;
using Erp.Application.Common;
using Erp.Application.Inventory;
using Erp.Application.Items;
using Erp.Application.Mrp;
using Erp.Application.Production;
using Erp.Application.Purchasing;
using Erp.Application.Quality;
using Erp.Infrastructure;
using Erp.Infrastructure.AI;
using Erp.Infrastructure.Json;
using Erp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("ErpDatabase")
    ?? throw new InvalidOperationException("缺少連線字串 ConnectionStrings:ErpDatabase");

// 列舉一律輸出字串。輸出數字的話，Phase 2 的 LLM 拿到 "status": 1 無從判讀，
// 正好違反「不留模糊解讀空間」的設計原則。
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());

    // 數量欄位輸出 30 而不是 30.0，理由見 NormalizedDecimalConverter
    options.SerializerOptions.Converters.Add(new NormalizedDecimalConverter());
});

builder.Services.AddApplication();
builder.Services.AddInfrastructure(connectionString);

// 例外 → HTTP 狀態碼的統一對映。沒有這層時查無料號會回 500 並洩漏堆疊
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ErpExceptionHandler>();
builder.Services.AddAiAssistant(builder.Configuration);

var app = builder.Build();

app.UseExceptionHandler();

// 開發環境自動建表並灌入展示資料；正式環境應改為明確的部署步驟
if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ErpDbContext>();
    await db.Database.MigrateAsync();
    await ErpDbSeeder.SeedAsync(db, scope.ServiceProvider.GetRequiredService<IClock>());
}

// 啟動時把 AI 助理的生效設定印出來（金鑰只印有沒有、不印值）。
// 沒有這行的話，「user-secrets 到底有沒有被讀到」只能靠猜。
var aiOptions = app.Services.GetRequiredService<IOptions<AiAssistantOptions>>().Value;
app.Logger.LogInformation(
    "AI 助理設定：模型 {Model}，工具迴圈上限 {MaxIterations} 輪，逾時 {TimeoutSeconds} 秒，API 金鑰來源：{KeySource}",
    aiOptions.Model, aiOptions.MaxToolIterations, aiOptions.TimeoutSeconds, DescribeKeySource(aiOptions));

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// AI 助理：Phase 2 的端到端入口。單輪問答，無對話上下文（已知限制）
app.MapPost("/api/ai-assistant/ask", async (
        AskRequest request, IAiAssistantService assistant, CancellationToken ct) =>
    {
        if (string.IsNullOrWhiteSpace(request.Question))
        {
            return Results.BadRequest(new { error = "question 不可為空" });
        }

        // LlmUnavailableException 由 ErpExceptionHandler 統一對映成 503
        var answer = await assistant.AskAsync(request.Question, ct);
        return Results.Ok(new { answer });
    });

// 以下端點目前是給人驗證資料層用的；Phase 2 的 AI 助理會改用同一批 Application 服務
app.MapGet("/api/items/search", async (string keyword, ItemMasterQueryService service, CancellationToken ct)
    => Results.Ok(await service.SearchByKeywordAsync(keyword, ct)));

app.MapGet("/api/items/{itemCode}/inventory", async (string itemCode, InventoryQueryService service, CancellationToken ct)
    => Results.Ok(await service.GetStockAsync(itemCode, ct)));

app.MapGet("/api/items/{itemCode}/sufficiency", async (
        string itemCode, decimal? plannedQty, BomExplosionService service, CancellationToken ct)
    => Results.Ok(await service.CalculateMaxBuildableAsync(itemCode, plannedQty, ct)));

app.MapGet("/api/work-orders/{workOrderNo}/progress", async (
        string workOrderNo, WorkOrderProgressService service, CancellationToken ct)
    => Results.Ok(await service.GetProgressAsync(workOrderNo, ct)));

app.MapGet("/api/work-orders/at-risk", async (
        DateOnly? from, DateOnly? to, WorkOrderRiskService service, CancellationToken ct)
    => Results.Ok(await service.GetAtRiskWorkOrdersAsync(from, to, ct)));

app.MapGet("/api/mrp/shortages", async (
        int? planningHorizonDays, string? itemCode, MrpCalculationService service, CancellationToken ct)
    => Results.Ok(await service.RunShortageAnalysisAsync(planningHorizonDays, itemCode, ct)));

app.MapGet("/api/purchase-orders/open", async (
        string? supplierCode, string? itemCode, PurchasingQueryService service, CancellationToken ct)
    => Results.Ok(await service.GetOpenPurchaseOrdersAsync(supplierCode, itemCode, ct)));

app.MapGet("/api/quality/summary", async (
        string? itemCode, string? workOrderNo, DateOnly? from, DateOnly? to,
        QualityInspectionQueryService service, CancellationToken ct)
    => Results.Ok(await service.GetSummaryAsync(itemCode, workOrderNo, from, to, ct)));

app.Run();

// 讓整合測試能參考這個 Program 類別
public partial class Program;

public sealed record AskRequest(string Question);

public partial class Program
{
    /// 只回報金鑰「從哪裡來」，永遠不印出金鑰本身
    private static string DescribeKeySource(AiAssistantOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            return "設定檔或 user-secrets";
        }

        return string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"))
            ? "未設定（將交由 SDK 自行解析憑證，若無憑證會回 503）"
            : "環境變數 ANTHROPIC_API_KEY";
    }
}
