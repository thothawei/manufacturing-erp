using System.Text.Json.Serialization;
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
using Scalar.AspNetCore;

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

builder.Services.AddOpenApi(options =>
{
    // 預設的文件標題是組件名稱（Erp.Api），改成看得懂的名字與說明
    options.AddDocumentTransformer((document, _, _) =>
    {
        document.Info.Title = "製造業 ERP API";
        document.Info.Version = "v1";
        document.Info.Description =
            "Clean Architecture 分層的製造業 ERP。\n\n"
            + "除了一般的查詢端點，另有一個以 tool-use 驅動的 AI 助理："
            + "使用者用自然語言提問，AI 透過八個唯讀工具查詢系統資料後回答，"
            + "所有數字都由後端算好，LLM 不做任何計算。\n\n"
            + "**兩個容易答錯的地方**：可行性判斷一律以可用庫存（帳上減已保留）為準；"
            + "多階 BOM 的用量以最終成品一個單位為分母，中間階已逐層累乘。";
        return Task.CompletedTask;
    });
});
builder.Services.AddAiAssistant(builder.Configuration);

var app = builder.Build();

app.UseExceptionHandler();

// 互動式 API 文件。只在開發環境開放 —— 正式環境不需要把端點結構公開出去。
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference(options => options
        .WithTitle("製造業 ERP API")
        .WithTheme(ScalarTheme.BluePlanet));
}

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

app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
    .WithSummary("健康檢查")
    .WithDescription("確認服務是否存活。");

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
    })
    .WithSummary("AI 助理問答")
    .WithDescription(
        "以自然語言提問，AI 透過八個唯讀工具查詢系統資料後回答。" +
        "一次請求內部會有多輪 LLM 與工具的往返（上限 5 輪），但不保存跨請求的對話記憶。" +
        "需要設定 Anthropic API 金鑰，未設定時回 503。");

// 以下端點目前是給人驗證資料層用的；Phase 2 的 AI 助理會改用同一批 Application 服務
app.MapGet("/api/items/search", async (string keyword, ItemMasterQueryService service, CancellationToken ct)
    => Results.Ok(await service.SearchByKeywordAsync(keyword, ct)))
    .WithSummary("搜尋料件")
    .WithDescription("以料號或品名的關鍵字搜尋料件主檔。查無相符時回傳空陣列。");

app.MapGet("/api/items/{itemCode}/inventory", async (string itemCode, InventoryQueryService service, CancellationToken ct)
    => Results.Ok(await service.GetStockAsync(itemCode, ct)))
    .WithSummary("查詢庫存")
    .WithDescription(
        "回傳帳上庫存、已被其他工單保留的數量、以及可用庫存（帳上減保留）。" +
        "回答「還有多少可以用」要看 available_qty。料號不存在時回 404。");

app.MapGet("/api/items/{itemCode}/sufficiency", async (
        string itemCode, decimal? plannedQty, BomExplosionService service, CancellationToken ct)
    => Results.Ok(await service.CalculateMaxBuildableAsync(itemCode, plannedQty, ct)))
    .WithSummary("可製造量試算")
    .WithDescription(
        "展開多階 BOM，以可用庫存計算最多能做幾個，並列出瓶頸原料。" +
        "帶入 plannedQty 時額外回報該數量是否足夠。" +
        "缺料清單的 requiredPerFinishedUnit 分母是最終成品一個單位，中間階用量已逐層累乘。" +
        "對沒有 BOM 的料件（例如原物料）呼叫會回 409。");

app.MapGet("/api/work-orders/{workOrderNo}/progress", async (
        string workOrderNo, WorkOrderProgressService service, CancellationToken ct)
    => Results.Ok(await service.GetProgressAsync(workOrderNo, ct)))
    .WithSummary("工單進度")
    .WithDescription("回傳工單狀態、發料狀態與各途程站別的完工數量。實際產出以最後一站為準。");

app.MapGet("/api/work-orders/at-risk", async (
        DateOnly? from, DateOnly? to, WorkOrderRiskService service, CancellationToken ct)
    => Results.Ok(await service.GetAtRiskWorkOrdersAsync(from, to, ct)))
    .WithSummary("風險工單清單")
    .WithDescription(
        "列出交期落在區間內、有延遲風險的未結案工單，不給區間時預設本週。" +
        "風險來源有兩種：已逾交期未完工，或剩餘產量的物料不足。" +
        "delayDays 為 0 代表有風險但目前還趕得上。");

app.MapGet("/api/mrp/shortages", async (
        int? planningHorizonDays, string? itemCode, MrpCalculationService service, CancellationToken ct)
    => Results.Ok(await service.RunShortageAnalysisAsync(planningHorizonDays, itemCode, ct)))
    .WithSummary("MRP 缺料試算")
    .WithDescription(
        "把規劃期間內未結案工單的剩餘產量展開成原料需求，扣掉可用庫存與能及時到貨的在途採購。" +
        "已逾期未結案的工單也會納入。suggestedOrderQty 已套用最小訂購量與訂購倍量，請直接引用。");

app.MapGet("/api/purchase-orders/open", async (
        string? supplierCode, string? itemCode, PurchasingQueryService service, CancellationToken ct)
    => Results.Ok(await service.GetOpenPurchaseOrdersAsync(supplierCode, itemCode, ct)))
    .WithSummary("未結案採購單")
    .WithDescription("列出已下單未到貨或部分入庫的採購單，依預計到貨日排序。");

app.MapGet("/api/quality/summary", async (
        string? itemCode, string? workOrderNo, DateOnly? from, DateOnly? to,
        QualityInspectionQueryService service, CancellationToken ct)
    => Results.Ok(await service.GetSummaryAsync(itemCode, workOrderNo, from, to, ct)))
    .WithSummary("品管檢驗彙總")
    .WithDescription("同一張工單的多次檢驗已由後端合併成一列，含不良原因彙整。");

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
