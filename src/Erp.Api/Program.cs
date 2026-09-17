using System.Text.Json.Serialization;
using Erp.Api.ErrorHandling;
using Erp.Application;
using Erp.Application.Abstractions;
using Erp.Application.Bom;
using Erp.Application.Common;
using Erp.Application.Inventory;
using Erp.Application.Items;
using Erp.Application.Ml;
using Erp.Application.Mrp;
using Erp.Application.Production;
using Erp.Application.Purchasing;
using Erp.Application.Quality;
using Erp.Domain.Purchasing;
using Erp.Infrastructure;
using Erp.Infrastructure.AI;
using Erp.Infrastructure.Json;
using Erp.Infrastructure.Persistence;
using Erp.Infrastructure.Rag;
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
            + "使用者用自然語言提問，AI 透過十二個工具查詢系統資料後回答，"
            + "所有數字都由後端算好，LLM 不做任何計算。\n\n"
            + "**兩個容易答錯的地方**：可行性判斷一律以可用庫存（帳上減已保留）為準；"
            + "多階 BOM 的用量以最終成品一個單位為分母，中間階已逐層累乘。";
        return Task.CompletedTask;
    });
});
builder.Services.AddAiAssistant(builder.Configuration);
builder.Services.AddDelayRiskModel();
builder.Services.AddMaterialDemandForecastModel();

var app = builder.Build();

app.UseExceptionHandler();

// 線上展示用的環境。
//
// 它與 Development 做同樣的兩件事（開放 Scalar、啟動時自動建表並灌展示資料），
// 但**刻意是一個獨立的名字**：正式環境不該有這兩者，而「demo 站需要它們」
// 與「開發機需要它們」是兩個不同的理由。合併成 IsDevelopment 的話，
// 日後要改其中一邊的行為就會連帶改到另一邊。
var isPublicDemo = app.Environment.IsEnvironment("Demo");
var showcaseMode = app.Environment.IsDevelopment() || isPublicDemo;

// 互動式 API 文件。只在開發與展示環境開放 —— 正式環境不需要把端點結構公開出去。
if (showcaseMode)
{
    app.MapOpenApi();
    app.MapScalarApiReference(options => options
        .WithTitle("製造業 ERP API")
        .WithTheme(ScalarTheme.BluePlanet));
}

// 開發與展示環境自動建表並灌入展示資料；正式環境應改為明確的部署步驟
var ragChunkCount = 0;
if (showcaseMode)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ErpDbContext>();
    await db.Database.MigrateAsync();
    await ErpDbSeeder.SeedAsync(db, scope.ServiceProvider.GetRequiredService<IClock>());

    // 文件檢索索引需要本機 Ollama 產生向量。沒裝時回 0 並只記一條 Warning ——
    // RAG 是可選模組，它建不起來不該讓整個服務啟動失敗
    ragChunkCount = await scope.ServiceProvider.GetRequiredService<RagIndexBuilder>().BuildAsync();
}

// 啟動時把 AI 助理的生效設定印出來（金鑰只印有沒有、不印值）。
// 沒有這行的話，「user-secrets 到底有沒有被讀到」只能靠猜。
var aiOptions = app.Services.GetRequiredService<IOptions<AiAssistantOptions>>().Value;
app.Logger.LogInformation(
    "AI 助理設定：模型 {Model}，端點 {Endpoint}，工具迴圈上限 {MaxIterations} 輪，逾時 {TimeoutSeconds} 秒，API 金鑰來源：{KeySource}",
    aiOptions.Model, DescribeEndpoint(aiOptions), aiOptions.MaxToolIterations, aiOptions.TimeoutSeconds,
    DescribeKeySource(aiOptions));

// 同理：沒有這行的話，「RAG 索引到底有沒有建起來」只能靠猜，
// 而它建不起來時的症狀是一個工具回錯誤，不是啟動失敗
var ragOptions = app.Services.GetRequiredService<IOptions<RagOptions>>().Value;
app.Logger.LogInformation(
    "文件語意檢索：索引 {ChunkCount} 段，模型 {Model}，相似度門檻 {Threshold}{Hint}",
    ragChunkCount, ragOptions.EmbeddingModel, ragOptions.SimilarityThreshold,
    ragChunkCount == 0
        ? $"（索引未建立：需要本機 Ollama 並執行 ollama pull {ragOptions.EmbeddingModel}；其餘工具不受影響）"
        : string.Empty);

// 展示站的首頁。
//
// 沒有它的話，訪客打開網址看到的是 404 —— 對一個「線上可試用」的連結來說
// 那是最糟的第一印象。這裡不做 HTML 頁面，回一份 JSON 導覽：
// 說清楚這是什麼、哪些端點可以直接點、以及哪些功能在這個環境下不會動、為什麼。
if (showcaseMode)
{
    app.MapGet("/", (IOptions<AiAssistantOptions> ai) => Results.Ok(new
    {
        name = "製造業 ERP + AI 助理（線上展示）",
        source = "https://github.com/thothawei/manufacturing-erp",
        interactiveDocs = "/scalar/v1",
        note = "展示資料在每次啟動時重建，隨便打不會弄壞任何東西。",
        tryThese = new[]
        {
            "/api/items/PANEL-01/inventory   可用庫存 vs 帳上庫存",
            "/api/items/TV-100/sufficiency   多階 BOM 展開，最多能做幾台",
            "/api/work-orders/at-risk        風險工單（逾期與缺料兩種來源）",
            "/api/mrp/shortages              MRP 缺料與建議採購量",
            "/api/mrp/time-phased            時間分期：第幾週開始缺",
            "/api/work-orders/{no}/delay-risk 規則式與 ML 模型並陳的延遲風險"
        },
        notAvailableHere = new[]
        {
            string.IsNullOrWhiteSpace(ai.Value.ApiKey)
                ? "POST /api/ai-assistant/ask —— 沒有設定 Anthropic 金鑰，會回 503。公開展示站刻意不放金鑰。"
                : "POST /api/ai-assistant/ask —— 已設定金鑰。",
            "search_documents（RAG）—— 需要本機 Ollama，容器裡沒有，會回 SERVICE_UNAVAILABLE。"
        }
    }))
    .WithSummary("展示站導覽")
    .WithDescription("線上展示環境的首頁：可以直接點的端點、以及這個環境下不會動的功能與原因。");
}

app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
    .WithSummary("健康檢查")
    .WithDescription("確認服務是否存活。");

// AI 助理：Phase 2 的端到端入口
app.MapPost("/api/ai-assistant/ask", async (
        AskRequest request, IAiAssistantService assistant, CancellationToken ct) =>
    {
        if (string.IsNullOrWhiteSpace(request.Question))
        {
            return Results.BadRequest(new { error = "question 不可為空" });
        }

        // 只接受伺服器自己發過的 GUID。放行任意字串的話，
        // 猜一個別人用過的 id 就能讀到別人的對話歷史。
        if (request.ConversationId is not null && !Guid.TryParse(request.ConversationId, out _))
        {
            return Results.BadRequest(new { error = "conversationId 必須是先前回應中回傳的識別碼" });
        }

        // 未知角色會擲 ArgumentException，由 ErpExceptionHandler 對映成 400
        // LlmUnavailableException 同樣由它對映成 503
        var result = await assistant.AskAsync(
            request.Question, request.ConversationId, request.Role, ct);
        return Results.Ok(new { answer = result.Answer, conversationId = result.ConversationId });
    })
    .WithSummary("AI 助理問答")
    .WithDescription(
        "以自然語言提問，AI 透過十二個工具查詢系統資料後回答（含一個本機向量檢索工具）。" +
        "其中十一個是唯讀的；唯一會寫入的 suggest_purchase_order 產生的是待人工確認的採購建議，" +
        "不會成立採購單。" +
        "一次請求內部會有多輪 LLM 與工具的往返（上限 5 輪）。" +
        "回應會帶一個 conversationId，下次請求帶著它就能接續同一段對話（記憶最近 6 輪問答，" +
        "存在記憶體、閒置 60 分鐘後丟棄，服務重啟即消失）。不帶或帶一個已失效的識別碼都會開始新對話。" +
        "可選的 role 參數會限制這次請求用得到哪些工具（production 生管／purchasing 採購／" +
        "quality 品保），不給則不限。這是工具層級的邊界，不是資料列層級的隔離 —— " +
        "允許的工具查得到全庫資料。不同角色的對話歷史互相隔離。" +
        "需要設定 Anthropic API 金鑰，未設定時回 503。");

app.MapGet("/api/work-orders/{workOrderNo}/delay-risk", async (
        string workOrderNo, WorkOrderDelayRiskPredictionService service, CancellationToken ct)
    => Results.Ok(await service.CompareAsync(workOrderNo, ct)))
    .WithSummary("工單延遲風險：規則式 vs 模型")
    .WithDescription(
        "同時給出規則式判斷（逾期／缺料，說得出理由）與機器學習模型的預測機率" +
        "（抓得到組合關聯，但說不出理由），讓兩者可以對照。" +
        "模型是離線訓練的 logistic regression，以 ONNX 格式載入；" +
        "**訓練資料是模擬的，不是真實產線資料**。" +
        "機率是排序用的參考值，不是「一定會延遲」——" +
        "它沒有經過校準（訓練時評估過 Platt 與 isotonic，數字不支持採用），" +
        "所以 0.68 代表「比 0.42 更值得先看」，不代表「六成八會延遲」。" +
        "outOfDistributionFeatures 會列出落在訓練資料分布之外的特徵：" +
        "模型對這種輸入照樣給得出機率，但可信度較低。" +
        "模型檔不存在時 predictedDelayProbability 為 null，規則式判斷照常可用。");

// 樣本數門檻：PSI 在小樣本下雜訊很大（幾筆觀察就能把某一箱的比例推得很極端），
// 30 不是統計上推導出來的臨界值，是「先求不要在樣本太少時誤報飄移」的工程判斷。
const int MinDriftSampleSize = 30;

// 跟 DependencyInjection.cs 的 RecentPredictionLogCapacity 對齊 ——
// 環狀緩衝區本身就只留得住這麼多筆，要更多也拿不到，這裡寫死同一個數字只是明講意圖。
const int DriftSampleWindow = 200;

// 模型註冊／健康檢查（S5 的一部分，見 docs/ml-dl-llm-strengthening-plan-v1.md）。
//
// 這裡回答的是「現在載進來的是哪個模型、跟程式碼對不對得上」——
// featureSchemaConsistent 是 false 時，available 也一定是 false：
// 特徵順序對不上代表模型會把數值餵進錯的欄位，那不是「品質比較差」，
// 是「這個結果沒有意義」，所以直接停用而不是照樣回傳一個機率。
// 自動重訓刻意不做，理由跟 ml-risk-prediction-module-plan-v1.md 第 10 節一致：
// 標籤延遲（工單要完工才知道有沒有延遲）讓「自動」重訓在這個規模下是假的。
// 漂移「偵測」則有做（PSI，見下方 BuildDriftInfo）——偵測到了之後要不要重訓仍是人的判斷。
app.MapGet("/api/ml/model-health", (
        IDelayRiskModel model, IRecentPredictionLog<WorkOrderDelayFeatures> recentPredictionLog) =>
    {
        var recent = recentPredictionLog.GetRecent(DriftSampleWindow);
        var drift = BuildDriftInfo(recent.Count, recent.Count < MinDriftSampleSize ? null : model.ComputeDrift(recent));

        return Results.Ok(new
        {
            available = model.IsAvailable,
            description = model.Description,
            isCalibrated = model.IsCalibrated,
            decisionThreshold = model.DecisionThreshold,
            featureSchemaConsistent = model.FeatureSchemaConsistent,
            trainedOn = model.TrainedOn,
            dataSource = model.DataSource,
            rowsTotal = model.RowsTotal,
            rocAuc = model.RocAuc,
            drift
        });
    })
    .WithSummary("延遲風險模型的註冊資訊與健康狀態")
    .WithDescription(
        "回答「現在載進來的是哪個模型、可不可信」，不是延遲風險預測本身" +
        "（預測請用 /api/work-orders/{workOrderNo}/delay-risk）。" +
        "featureSchemaConsistent 為 false 時 available 必定也是 false —— " +
        "特徵順序與程式碼對不上時，模型會把數值餵進錯的欄位，" +
        "為避免回傳一個外觀正常但語意錯誤的機率，直接停用預測。" +
        "沒有模型檔時多數欄位為 null，description 會說明原因。" +
        "drift 是最近一批推論輸入跟訓練分布的 PSI 比較（見 drift.note），" +
        "樣本數不夠時 drift.available 為 false，不代表沒有飄移，是還無法判斷。");

// 採購建議的人工確認流程。
//
// 這三個端點刻意**不是** AI 工具：AI 只能產生「待人工確認」的建議
// （suggest_purchase_order），把建議變成正式採購單必須有人按下核准。
// 即使 LLM 已經被限制只能呼叫工具、不能寫 SQL，寫入類的操作仍然多一層人工確認 ——
// 採購會產生對外的金錢承諾，而 LLM 的輸入（使用者的一句話、檢索到的文件內容）
// 都是它控制不了的。
app.MapGet("/api/purchase-suggestions", async (
        PurchaseSuggestionStatus? status, PurchaseSuggestionService service, CancellationToken ct)
    => Results.Ok(await service.ListAsync(status, ct)))
    .WithSummary("採購建議清單")
    .WithDescription(
        "列出 AI 產生的採購建議，可用 status 過濾（PendingApproval 待確認／Approved 已核准／" +
        "Rejected 已駁回）。待確認的建議尚未成立任何採購單。");

app.MapPost("/api/purchase-suggestions/{suggestionNo}/approve", async (
        string suggestionNo, DecisionRequest request,
        PurchaseSuggestionService service, CancellationToken ct)
    => Results.Ok(await service.ApproveAsync(suggestionNo, request.DecidedBy, ct)))
    .WithSummary("核准採購建議")
    .WithDescription(
        "把建議轉成正式採購單，回傳的 createdPoNo 就是新產生的採購單號。" +
        "這是整個系統唯一會新增採購單的路徑，AI 的工具目錄裡沒有任何東西通得到這裡。" +
        "已核准或已駁回的建議不能重複處理（回 409）—— 重複核准會變成兩張採購單。");

app.MapPost("/api/purchase-suggestions/{suggestionNo}/reject", async (
        string suggestionNo, DecisionRequest request,
        PurchaseSuggestionService service, CancellationToken ct)
    => Results.Ok(await service.RejectAsync(suggestionNo, request.DecidedBy, ct)))
    .WithSummary("駁回採購建議")
    .WithDescription("把建議標記為已駁回，不會產生採購單。已處理過的建議不能重複駁回（回 409）。");

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
        DateOnly? from, DateOnly? to, int? windowDays,
        WorkOrderRiskService service, CancellationToken ct)
    => Results.Ok(await service.GetAtRiskWorkOrdersAsync(from, to, windowDays, ct)))
    .WithSummary("風險工單清單")
    .WithDescription(
        "列出交期落在區間內、有延遲風險的未結案工單。" +
        "區間三種給法：明確的 from／to、從今天起算的 windowDays（1-365），" +
        "或都不給（預設查到本週日為止）。同時給時以 from／to 為準。" +
        "不指定 from 時，已逾交期但尚未結案的舊工單一律納入 —— 它們是最急的風險。" +
        "風險來源有兩種：已逾交期未完工，或剩餘產量的物料不足。" +
        "delayDays 為 0 代表有風險但目前還趕得上。");

app.MapGet("/api/mrp/time-phased", async (
        int? weeks, string? itemCode, MrpCalculationService service, CancellationToken ct)
    => Results.Ok(await service.RunTimePhasedAnalysisAsync(weeks, itemCode, ct)))
    .WithSummary("MRP 時間分期試算")
    .WithDescription(
        "回答「什麼時候會開始缺料」。把未來切成以週為單位的時間桶（預設 8 週，今天起算每 7 天），" +
        "每桶累計預期入庫與需求，算出期末預估庫存水位，並指出水位第一次轉負的週次。" +
        "逾期未結案的需求與早該到卻未到的採購單都算在第 1 桶。不產生建議採購量 —— " +
        "那是 /api/mrp/shortages 的職責。");

app.MapGet("/api/mrp/shortages", async (
        int? planningHorizonDays, string? itemCode, MrpCalculationService service, CancellationToken ct)
    => Results.Ok(await service.RunShortageAnalysisAsync(planningHorizonDays, itemCode, ct)))
    .WithSummary("MRP 缺料試算")
    .WithDescription(
        "把規劃期間內未結案工單的剩餘產量展開成原料需求，扣掉可用庫存與能及時到貨的在途採購。" +
        "已逾期未結案的工單也會納入；已全數發料的工單則不列入需求 —— " +
        "那些料已經出庫、反映在帳上庫存的減少裡了，再算一次會讓缺料量偏高。" +
        "suggestedOrderQty 已套用最小訂購量與訂購倍量，請直接引用。" +
        "這裡回答的是「總共缺多少」；「什麼時候開始缺」請用 /api/mrp/time-phased。");

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

// 物料需求時間序列預測（S6，見 docs/material-demand-forecast-plan-v1.md）。
//
// 這個端點刻意是 POST 而不是 GET /api/items/{itemCode}/demand-forecast：
// 這個系統目前沒有任何地方持久化「歷史週別實際需求」，模型需要的 lag/移動平均特徵
// 沒有地方可以在伺服器這一側查出來，只能由呼叫端算好、隨請求提供。
// 這不是偷懶少做一步，是誠實反映「這個資料還不存在」——見上面文件第 5 節的說明。
app.MapPost("/api/ml/demand-forecast", (
        DemandForecastRequest request, IMaterialDemandForecastModel model,
        IRecentPredictionLog<MaterialDemandForecastFeatures> recentPredictionLog) =>
    {
        if (!MaterialDemandForecastFeatures.KnownItems.Contains(request.ItemCode))
        {
            return Results.BadRequest(new
            {
                error = $"itemCode 必須是 {string.Join("、", MaterialDemandForecastFeatures.KnownItems)} 之一，"
                    + $"收到的是 {request.ItemCode}"
            });
        }

        var features = MaterialDemandForecastFeatures.ForItem(
            request.ItemCode, request.Lag1, request.Lag2, request.Lag3, request.Lag4, request.Lag52,
            request.RollingMean4, request.RollingMean12, request.WeekOfYear);

        // 漂移偵測（S5）要看的是線上實際收到的輸入，跟模型當下可不可用是兩件事，
        // 理由跟 WorkOrderDelayRiskPredictionService 那邊一樣，記錄不等預測成功再做
        recentPredictionLog.Record(features);

        return Results.Ok(new
        {
            itemCode = request.ItemCode,
            // seasonal naive 基準線不需要模型，就是「去年同一週」那個數字本身
            seasonalNaiveDemand = request.Lag52,
            predictedDemand = model.IsAvailable ? model.PredictDemand(features) : (double?)null,
            deployedModel = model.DeployedModel,
            winnerByMape = model.WinnerByMape,
            modelAvailable = model.IsAvailable,
            description = model.Description
        });
    })
    .WithSummary("物料需求預測：seasonal naive vs GBDT")
    .WithDescription(
        "同時給出 seasonal naive 基準線（去年同一週的實際值）與 GBDT 模型的預測值。" +
        "**特徵由呼叫端提供，不是伺服器算的**：這個系統目前沒有持久化歷史週別需求，" +
        "沒有地方可以在請求當下重新查出 lag/移動平均特徵。" +
        "winnerByMape 是訓練時三方對照（seasonal naive／GBDT／小型 DL）依平均 MAPE 選出的贏家，" +
        "不一定等於 deployedModel（目前固定部署 gbdt，理由見 metadata 的 deploymentNote）。" +
        "itemCode 只認得三個訓練過的料號，其餘一律回 400。" +
        "**訓練資料是模擬的，不是真實產線資料。**");

// 物料需求預測模型的註冊資訊與健康狀態，跟延遲風險那邊的 /api/ml/model-health 對稱（S5）。
// 拆成獨立端點而不是塞進 /api/ml/demand-forecast 的回應，理由是後者需要呼叫端提供特徵
// 才問得出來，而「模型現在健不健康、最近有沒有飄移」是即使沒人在預測也該問得到的狀態。
app.MapGet("/api/ml/demand-forecast-health", (
        IMaterialDemandForecastModel model,
        IRecentPredictionLog<MaterialDemandForecastFeatures> recentPredictionLog) =>
    {
        var recent = recentPredictionLog.GetRecent(DriftSampleWindow);
        var drift = BuildDriftInfo(recent.Count, recent.Count < MinDriftSampleSize ? null : model.ComputeDrift(recent));

        return Results.Ok(new
        {
            available = model.IsAvailable,
            description = model.Description,
            featureSchemaConsistent = model.FeatureSchemaConsistent,
            trainedOn = model.TrainedOn,
            winnerByMape = model.WinnerByMape,
            deployedModel = model.DeployedModel,
            drift
        });
    })
    .WithSummary("物料需求預測模型的註冊資訊與健康狀態")
    .WithDescription(
        "回答「現在載進來的是哪個模型、可不可信」，不是需求預測本身" +
        "（預測請用 POST /api/ml/demand-forecast）。" +
        "featureSchemaConsistent 為 false 時 available 必定也是 false，理由與延遲風險模型一致。" +
        "drift 是最近一批推論輸入跟訓練分布的 PSI 比較，樣本數不夠時 drift.available 為 false。");

app.Run();

// model-health 與 demand-forecast-health 共用同一套「樣本夠不夠、飄移嚴不嚴重」判讀邏輯，
// 避免兩個端點各寫一份、之後改門檻只改到一邊。
static object BuildDriftInfo(int recentSampleCount, IReadOnlyDictionary<string, double>? perFeaturePsi)
{
    if (recentSampleCount < MinDriftSampleSize)
    {
        return new
        {
            available = false,
            recentSampleCount,
            minSampleSize = MinDriftSampleSize,
            perFeaturePsi = (IReadOnlyDictionary<string, double>?)null,
            significantDriftFeatures = Array.Empty<string>(),
            note = $"最近推論樣本數（{recentSampleCount}）還不到 {MinDriftSampleSize} 筆，" +
                "PSI 在小樣本下不穩定，先不計算——這不代表沒有飄移，是還無法判斷。"
        };
    }

    if (perFeaturePsi is null)
    {
        return new
        {
            available = false,
            recentSampleCount,
            minSampleSize = MinDriftSampleSize,
            perFeaturePsi = (IReadOnlyDictionary<string, double>?)null,
            significantDriftFeatures = Array.Empty<string>(),
            note = "模型沒有 metadata（或 metadata 裡沒有 drift bin edges），沒有訓練分布可以比較。"
        };
    }

    var significant = perFeaturePsi
        .Where(kv => kv.Value >= PsiCalculator.SignificantThreshold)
        .Select(kv => kv.Key)
        .ToArray();

    return new
    {
        available = true,
        recentSampleCount,
        minSampleSize = MinDriftSampleSize,
        perFeaturePsi,
        significantDriftFeatures = significant,
        note = $"PSI < {PsiCalculator.ModerateThreshold} 沒有顯著變化、" +
            $"{PsiCalculator.ModerateThreshold}~{PsiCalculator.SignificantThreshold} 中度飄移值得留意、" +
            $">= {PsiCalculator.SignificantThreshold} 顯著飄移（訓練分布可能已經不能代表現在的輸入）。" +
            (significant.Length > 0
                ? $" 目前有 {significant.Length} 個特徵超過顯著門檻：{string.Join("、", significant)}。"
                : " 目前沒有特徵超過顯著門檻。")
    };
}

// 讓整合測試能參考這個 Program 類別
public partial class Program;

public sealed record AskRequest(string Question, string? ConversationId = null, string? Role = null);

/// 核准／駁回時要指明是誰做的決定。沒有身分驗證，所以這是一筆稽核紀錄，
/// 不是一道權限檢查 —— README 的已知限制有寫明。
public sealed record DecisionRequest(string DecidedBy);

/// 物料需求預測的請求。所有 lag/移動平均特徵由呼叫端算好提供，理由見端點說明。
public sealed record DemandForecastRequest(
    string ItemCode,
    double Lag1, double Lag2, double Lag3, double Lag4, double Lag52,
    double RollingMean4, double RollingMean12, int WeekOfYear);

public partial class Program
{
    /// 打官方還是打本機 gateway，是最容易設錯又最難從回應看出來的一件事 ——
    /// gateway 沒開時只會得到一句「無法連線」，看不出它本來想連去哪
    private static string DescribeEndpoint(AiAssistantOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            return "Anthropic 官方";
        }

        var fallback = options.UseServerSideFallback ? "開啟" : "關閉";
        return $"{options.BaseUrl}（server-side refusal fallback {fallback}）";
    }

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
