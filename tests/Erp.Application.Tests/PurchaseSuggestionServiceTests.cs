using Erp.Application.Bom;
using Erp.Application.Common;
using Erp.Application.Mrp;
using Erp.Application.Purchasing;
using Erp.Application.Tests.Fakes;
using Erp.Domain.Inventory;
using Erp.Domain.Production;
using Erp.Domain.Purchasing;

namespace Erp.Application.Tests;

/// 採購建議：AI 寫建議，人工核准才成立採購單。
///
/// 這組測試釘的是那條分界線本身 —— 建議這一側可以錯、可以重複、可以被駁回，
/// 因為它不對外承諾任何事；採購單那一側只能由 ApproveAsync 產生，
/// 而 ApproveAsync 沒有任何 AI 工具通得到。
public class PurchaseSuggestionServiceTests
{
    private static readonly DateOnly Today = new(2026, 9, 10);

    private InMemoryPurchaseOrderRepository _purchaseOrders = null!;
    private InMemoryPurchaseSuggestionRepository _suggestions = null!;

    /// 100 台 TV-100 需要 200 片面板，可用 50 片 → 淨缺 150，
    /// 訂購倍量 50 → 建議 150 片
    private PurchaseSuggestionService CreateService(
        IEnumerable<PurchaseSuggestion>? existingSuggestions = null,
        IEnumerable<PurchaseOrder>? existingPurchaseOrders = null)
    {
        var itemRepo = new InMemoryItemRepository(TestData.Items, TestData.SupplyInfos);
        var inventoryRepo = new InMemoryInventoryRepository(
            [TestData.Balance("PANEL-01", 50m), TestData.Balance("SCREW-05", 100_000m)]);
        var bomService = new BomExplosionService(itemRepo, new InMemoryBomRepository(TestData.Bom), inventoryRepo);

        _purchaseOrders = new InMemoryPurchaseOrderRepository(existingPurchaseOrders ?? []);
        _suggestions = new InMemoryPurchaseSuggestionRepository(existingSuggestions);

        var workOrders = new InMemoryWorkOrderRepository(
        [
            new WorkOrder
            {
                WorkOrderNo = "WO-01", ItemCode = "TV-100", PlannedQty = 100m,
                DueDate = new DateOnly(2026, 9, 20), Status = WorkOrderStatus.Released
            }
        ], []);

        var mrp = new MrpCalculationService(
            workOrders, itemRepo, inventoryRepo, _purchaseOrders, bomService, new FakeClock(Today));

        return new PurchaseSuggestionService(
            mrp, _suggestions, _purchaseOrders, itemRepo, new FakeClock(Today));
    }

    [Fact]
    public async Task 產生的建議一律是待人工確認狀態_而且不會有採購單()
    {
        var result = await CreateService().SuggestFromShortagesAsync();

        var panel = result.Created.Single(s => s.ItemCode == "PANEL-01");
        Assert.Equal("PendingApproval", panel.Status);
        Assert.Equal(150m, panel.SuggestedQty);        // 淨缺 150、訂購倍量 50
        Assert.Equal("SUP-008", panel.SupplierCode);
        Assert.Null(panel.CreatedPoNo);

        Assert.Empty(_purchaseOrders.All);             // 一張採購單都沒有
        Assert.Contains("必須由人", result.Note);
    }

    [Fact]
    public async Task 建議理由由後端從MRP結果組出來()
    {
        // 理由不是 LLM 寫的 —— 它是給人看的依據，必須對得上後端算的數字
        var result = await CreateService().SuggestFromShortagesAsync();

        var reason = result.Created.Single(s => s.ItemCode == "PANEL-01").Reason;

        Assert.Contains("毛需求 200", reason);
        Assert.Contains("可用庫存 50", reason);
        Assert.Contains("淨缺 150", reason);
    }

    [Fact]
    public async Task 同一個料號已經有待確認的建議時不會重複產生()
    {
        // LLM 重複呼叫同一個工具是很常見的事 —— 它看不到上一次呼叫的副作用
        var service = CreateService(
        [
            new PurchaseSuggestion
            {
                SuggestionNo = "PS-20260910-001", ItemCode = "PANEL-01", SuggestedQty = 150m,
                NeededByDate = new DateOnly(2026, 9, 20), Reason = "先前的建議",
                CreatedOn = Today
            }
        ]);

        var result = await service.SuggestFromShortagesAsync();

        Assert.Empty(result.Created);
        Assert.Contains("PANEL-01", result.SkippedItemCodes);
        Assert.Single(_suggestions.All);
    }

    [Fact]
    public async Task 已經被駁回的建議不會擋住新的建議()
    {
        // 駁回代表「這次不買」，不代表「以後都不要再提」
        var service = CreateService(
        [
            new PurchaseSuggestion
            {
                SuggestionNo = "PS-20260909-001", ItemCode = "PANEL-01", SuggestedQty = 150m,
                NeededByDate = new DateOnly(2026, 9, 20), Reason = "上次的建議",
                CreatedOn = new DateOnly(2026, 9, 9),
                Status = PurchaseSuggestionStatus.Rejected
            }
        ]);

        var result = await service.SuggestFromShortagesAsync();

        Assert.Contains(result.Created, s => s.ItemCode == "PANEL-01");
    }

    [Fact]
    public async Task 沒有缺料時不產生建議_這是正常結果不是失敗()
    {
        var itemRepo = new InMemoryItemRepository(TestData.Items, TestData.SupplyInfos);
        var inventoryRepo = new InMemoryInventoryRepository(
            [TestData.Balance("PANEL-01", 100_000m), TestData.Balance("SCREW-05", 100_000m)]);
        var bomService = new BomExplosionService(itemRepo, new InMemoryBomRepository(TestData.Bom), inventoryRepo);
        var purchaseOrders = new InMemoryPurchaseOrderRepository([]);
        var suggestions = new InMemoryPurchaseSuggestionRepository();

        var mrp = new MrpCalculationService(
            new InMemoryWorkOrderRepository([], []), itemRepo, inventoryRepo,
            purchaseOrders, bomService, new FakeClock(Today));

        var service = new PurchaseSuggestionService(
            mrp, suggestions, purchaseOrders, itemRepo, new FakeClock(Today));

        var result = await service.SuggestFromShortagesAsync();

        Assert.Empty(result.Created);
        Assert.Contains("沒有缺料", result.Note);
    }

    [Fact]
    public async Task 核准之後才會產生正式採購單()
    {
        var service = CreateService();
        var created = (await service.SuggestFromShortagesAsync()).Created
            .Single(s => s.ItemCode == "PANEL-01");

        Assert.Empty(_purchaseOrders.All);

        var approved = await service.ApproveAsync(created.SuggestionNo, "王採購");

        Assert.Equal("Approved", approved.Status);
        Assert.Equal("王採購", approved.DecidedBy);
        Assert.Equal(Today, approved.DecidedOn);

        var po = Assert.Single(_purchaseOrders.All);
        Assert.Equal(approved.CreatedPoNo, po.PoNo);
        Assert.Equal("PANEL-01", po.ItemCode);
        Assert.Equal(150m, po.OrderedQty);
        Assert.Equal(0m, po.ReceivedQty);
        Assert.Equal(PurchaseOrderStatus.Open, po.Status);

        // 交期是「今天 + 採購前置期（5 天）」，不是建議裡的需求日 ——
        // 需求日是「什麼時候要用到」，兩者混用會讓下一輪 MRP 把來不及的單算成及時供給
        Assert.Equal(Today.AddDays(5), po.ExpectedArrivalDate);
    }

    [Fact]
    public async Task 重複核准會被擋下來_否則會變成兩張採購單()
    {
        var service = CreateService();
        var created = (await service.SuggestFromShortagesAsync()).Created[0];

        await service.ApproveAsync(created.SuggestionNo, "王採購");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ApproveAsync(created.SuggestionNo, "李採購"));

        Assert.Single(_purchaseOrders.All);
    }

    [Fact]
    public async Task 核准過的建議不能再被駁回()
    {
        var service = CreateService();
        var created = (await service.SuggestFromShortagesAsync()).Created[0];

        await service.ApproveAsync(created.SuggestionNo, "王採購");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RejectAsync(created.SuggestionNo, "李採購"));
    }

    [Fact]
    public async Task 駁回不會產生採購單()
    {
        var service = CreateService();
        var created = (await service.SuggestFromShortagesAsync()).Created[0];

        var rejected = await service.RejectAsync(created.SuggestionNo, "李採購");

        Assert.Equal("Rejected", rejected.Status);
        Assert.Null(rejected.CreatedPoNo);
        Assert.Empty(_purchaseOrders.All);
    }

    [Fact]
    public async Task 查無建議時擲出查無資料()
    {
        var service = CreateService();

        await Assert.ThrowsAsync<EntityNotFoundException>(
            () => service.ApproveAsync("PS-NOT-EXIST", "王採購"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task 沒有指明是誰做的決定時擲出參數例外(string decidedBy)
    {
        var service = CreateService();
        var created = (await service.SuggestFromShortagesAsync()).Created[0];

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.ApproveAsync(created.SuggestionNo, decidedBy));
    }

    [Fact]
    public async Task 採購單號接續當天既有的單號編下去()
    {
        var service = CreateService(existingPurchaseOrders:
        [
            new PurchaseOrder
            {
                PoNo = "PO-20260910-001", SupplierCode = "SUP-008", ItemCode = "CABLE-07",
                OrderedQty = 10m, ReceivedQty = 0m,
                ExpectedArrivalDate = new DateOnly(2026, 9, 20), Status = PurchaseOrderStatus.Open
            }
        ]);

        var created = (await service.SuggestFromShortagesAsync()).Created[0];
        var approved = await service.ApproveAsync(created.SuggestionNo, "王採購");

        Assert.Equal("PO-20260910-002", approved.CreatedPoNo);
    }
}
