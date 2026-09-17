using Erp.Application.Bom;
using Erp.Application.Common;
using Erp.Application.Inventory;
using Erp.Application.Items;
using Erp.Application.Ml;
using Erp.Application.Mrp;
using Erp.Application.Production;
using Erp.Application.Purchasing;
using Erp.Application.Quality;
using Erp.Infrastructure.AI;
using Erp.Infrastructure.Ml;
using Erp.Infrastructure.Persistence.Repositories;
using Erp.Infrastructure.Rag;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Erp.Infrastructure.Tests;

internal static class TestServices
{
    /// 用真的 EF Core Repository 組出完整的 ToolDispatcher。
    /// 集中一處，之後新增工具只要改這裡一個地方。
    public static ToolDispatcher CreateDispatcher(
        SqliteTestDatabase fixture,
        IClock clock,
        ILogger<ToolDispatcher>? logger = null,
        IEmbeddingClient? embeddingClient = null,
        RagOptions? ragOptions = null,
        IDelayRiskModel? delayRiskModel = null,
        IRecentPredictionLog<WorkOrderDelayFeatures>? recentPredictionLog = null)
    {
        var db = fixture.CreateContext();

        var itemRepository = new ItemRepository(db);
        var inventoryRepository = new InventoryRepository(db);
        var workOrderRepository = new WorkOrderRepository(db);
        var purchaseOrderRepository = new PurchaseOrderRepository(db);

        var bomExplosionService = new BomExplosionService(itemRepository, new BomRepository(db), inventoryRepository);

        var workOrderRiskService = new WorkOrderRiskService(
            workOrderRepository, itemRepository, bomExplosionService, clock);

        var mrpCalculationService = new MrpCalculationService(
            workOrderRepository, itemRepository, inventoryRepository,
            purchaseOrderRepository, bomExplosionService, clock);

        return new ToolDispatcher(
            new ItemMasterQueryService(itemRepository),
            new InventoryQueryService(itemRepository, inventoryRepository, clock),
            bomExplosionService,
            new WorkOrderProgressService(workOrderRepository),
            workOrderRiskService,
            mrpCalculationService,
            new PurchasingQueryService(purchaseOrderRepository),
            new PurchaseSuggestionService(
                mrpCalculationService,
                new PurchaseSuggestionRepository(db),
                purchaseOrderRepository,
                itemRepository,
                clock),
            new WorkOrderDelayRiskPredictionService(
                workOrderRepository, itemRepository, bomExplosionService, workOrderRiskService,
                delayRiskModel ?? CreateDelayRiskModel(),
                recentPredictionLog ?? new InMemoryRecentPredictionLog<WorkOrderDelayFeatures>(200),
                clock),
            new QualityInspectionQueryService(new QualityInspectionRepository(db)),
            CreateSearchService(fixture, embeddingClient ?? new FakeEmbeddingClient(), ragOptions),
            logger ?? NullLogger<ToolDispatcher>.Instance);
    }

    /// 測試預設用一個乾淨的對話記憶。多輪對話測試會自己傳一個共用的進去 ——
    /// 每次呼叫都建新的話，就永遠測不到「上一輪記住了什麼」。
    public static IConversationStore CreateConversationStore(int maxTurns = 6)
        => new InMemoryConversationStore(maxTurns, maxConversations: 200, idleTimeout: TimeSpan.FromMinutes(60));

    /// 真的載入 repo 裡那個 ONNX 模型。
    ///
    /// 刻意不用假模型：這裡要驗的就是「模型檔真的載得起來、推論真的跑得動」——
    /// 用假的推論器會讓 ONNX 那一整段完全沒被測到。
    public static IDelayRiskModel CreateDelayRiskModel()
        => new OnnxDelayRiskModel(NullLogger<OnnxDelayRiskModel>.Instance);

    public static DocumentSearchService CreateSearchService(
        SqliteTestDatabase fixture,
        IEmbeddingClient embeddingClient,
        RagOptions? ragOptions = null,
        ILogger<DocumentSearchService>? logger = null)
        => new(
            fixture.CreateContext(),
            embeddingClient,
            Options.Create(ragOptions ?? new RagOptions()),
            logger ?? NullLogger<DocumentSearchService>.Instance);

    /// 用假 embedding 把展示語料灌進索引，讓檢索相關的測試離線可跑
    public static Task<int> BuildRagIndexAsync(
        SqliteTestDatabase fixture, IEmbeddingClient embeddingClient)
        => new RagIndexBuilder(
            fixture.CreateContext(), embeddingClient, NullLogger<RagIndexBuilder>.Instance).BuildAsync();
}
