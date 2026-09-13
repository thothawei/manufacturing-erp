using System.Text.Json;
using Erp.Application.Bom;
using Erp.Application.Mrp;
using Erp.Application.Purchasing;
using Erp.Domain.Purchasing;
using Erp.Infrastructure.AI;
using Erp.Infrastructure.Persistence;
using Erp.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Erp.Infrastructure.Tests;

/// 走真的 EF Core：AI 工具產生建議 → 人工核准 → 正式採購單真的出現在資料表裡。
///
/// Application 層的測試用的是 in-memory 假 repository，驗不到兩件只有真 EF Core
/// 才會出事的東西：
/// 1. 讀建議時若加了 AsNoTracking，核准會「成功」但什麼都沒寫進去，而且不會有錯誤。
/// 2. 建議與採購單分屬兩次 SaveChanges，順序錯了會留下不一致的狀態。
public class PurchaseApprovalFlowTests : IAsyncLifetime
{
    private static readonly DateOnly Today = new(2026, 9, 10);

    private SqliteTestDatabase _fixture = null!;
    private ToolDispatcher _dispatcher = null!;

    public async Task InitializeAsync()
    {
        _fixture = new SqliteTestDatabase();
        var clock = new TestClock(Today);
        await ErpDbSeeder.SeedAsync(_fixture.Db, clock);
        _dispatcher = TestServices.CreateDispatcher(_fixture, clock);
    }

    public async Task DisposeAsync() => await _fixture.DisposeAsync();

    private PurchaseSuggestionService CreateSuggestionService()
    {
        var db = _fixture.CreateContext();
        var clock = new TestClock(Today);

        var itemRepository = new ItemRepository(db);
        var inventoryRepository = new InventoryRepository(db);
        var purchaseOrderRepository = new PurchaseOrderRepository(db);

        var mrp = new MrpCalculationService(
            new WorkOrderRepository(db), itemRepository, inventoryRepository, purchaseOrderRepository,
            new BomExplosionService(itemRepository, new BomRepository(db), inventoryRepository),
            clock);

        return new PurchaseSuggestionService(
            mrp, new PurchaseSuggestionRepository(db), purchaseOrderRepository, itemRepository, clock);
    }

    [Fact]
    public async Task 透過AI工具產生建議_再由人工核准_採購單才真的出現在資料表裡()
    {
        var poCountBefore = await _fixture.CreateContext().PurchaseOrders.CountAsync();

        // 一、AI 工具產生建議
        var toolResult = await _dispatcher.ExecuteAsync(
            ToolCatalog.SuggestPurchaseOrder, JsonSerializer.SerializeToElement(new { }));

        Assert.False(toolResult.IsError);

        var payload = JsonDocument.Parse(toolResult.Content).RootElement;
        var created = payload.GetProperty("created");
        Assert.True(created.GetArrayLength() > 0);

        var suggestionNo = created[0].GetProperty("suggestion_no").GetString()!;
        Assert.Equal("PendingApproval", created[0].GetProperty("status").GetString());

        // note 會被原樣送給 LLM，它必須寫明還沒下單
        Assert.Contains("必須由人", payload.GetProperty("note").GetString()!);

        // 工具跑完，資料表裡有建議、但採購單一張都沒多
        var afterSuggest = _fixture.CreateContext();
        Assert.Equal(poCountBefore, await afterSuggest.PurchaseOrders.CountAsync());
        Assert.NotEmpty(await afterSuggest.PurchaseSuggestions.ToListAsync());

        // 二、人工核准
        var approved = await CreateSuggestionService().ApproveAsync(suggestionNo, "王採購");

        var afterApprove = _fixture.CreateContext();

        // 建議的狀態真的寫回資料庫了（AsNoTracking 的話這裡會是 PendingApproval）
        var stored = await afterApprove.PurchaseSuggestions
            .SingleAsync(s => s.SuggestionNo == suggestionNo);
        Assert.Equal(PurchaseSuggestionStatus.Approved, stored.Status);
        Assert.Equal("王採購", stored.DecidedBy);
        Assert.Equal(approved.CreatedPoNo, stored.CreatedPoNo);

        // 採購單真的多了一張，而且對得上建議的內容
        Assert.Equal(poCountBefore + 1, await afterApprove.PurchaseOrders.CountAsync());

        var po = await afterApprove.PurchaseOrders.SingleAsync(p => p.PoNo == approved.CreatedPoNo);
        Assert.Equal(stored.ItemCode, po.ItemCode);
        Assert.Equal(stored.SuggestedQty, po.OrderedQty);
        Assert.Equal(PurchaseOrderStatus.Open, po.Status);
    }

    [Fact]
    public async Task 駁回之後資料表裡不會多出採購單()
    {
        var poCountBefore = await _fixture.CreateContext().PurchaseOrders.CountAsync();

        await _dispatcher.ExecuteAsync(
            ToolCatalog.SuggestPurchaseOrder, JsonSerializer.SerializeToElement(new { }));

        var suggestionNo = (await _fixture.CreateContext().PurchaseSuggestions.FirstAsync()).SuggestionNo;

        await CreateSuggestionService().RejectAsync(suggestionNo, "李採購");

        var after = _fixture.CreateContext();
        var stored = await after.PurchaseSuggestions.SingleAsync(s => s.SuggestionNo == suggestionNo);

        Assert.Equal(PurchaseSuggestionStatus.Rejected, stored.Status);
        Assert.Null(stored.CreatedPoNo);
        Assert.Equal(poCountBefore, await after.PurchaseOrders.CountAsync());
    }

    [Fact]
    public async Task AI工具重複呼叫不會產生重複的建議()
    {
        // LLM 看不到上一次呼叫的副作用，重複呼叫同一個工具很常見
        await _dispatcher.ExecuteAsync(
            ToolCatalog.SuggestPurchaseOrder, JsonSerializer.SerializeToElement(new { }));
        var firstCount = await _fixture.CreateContext().PurchaseSuggestions.CountAsync();

        var second = await _dispatcher.ExecuteAsync(
            ToolCatalog.SuggestPurchaseOrder, JsonSerializer.SerializeToElement(new { }));

        Assert.Equal(firstCount, await _fixture.CreateContext().PurchaseSuggestions.CountAsync());

        var payload = JsonDocument.Parse(second.Content).RootElement;
        Assert.Equal(0, payload.GetProperty("created").GetArrayLength());
        Assert.True(payload.GetProperty("skipped_item_codes").GetArrayLength() > 0);
    }

    [Fact]
    public async Task 品保角色不能產生採購建議()
    {
        var result = await _dispatcher.ExecuteAsync(
            ToolCatalog.SuggestPurchaseOrder, JsonSerializer.SerializeToElement(new { }),
            AssistantScope.Quality);

        Assert.True(result.IsError);
        Assert.Contains("NOT_AUTHORIZED", result.Content);
        Assert.Empty(await _fixture.CreateContext().PurchaseSuggestions.ToListAsync());
    }
}
