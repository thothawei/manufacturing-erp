using Erp.Domain.Items;
using Erp.Domain.Production;
using Erp.Domain.Purchasing;
using Erp.Infrastructure.Persistence.Repositories;

namespace Erp.Infrastructure.Tests;

public class RepositoryTests
{
    private static readonly DateOnly Today = new(2026, 9, 10);

    [Fact]
    public async Task 工單查詢的未結案條件能翻譯成SQL且正確過濾狀態()
    {
        // IsOpen 是 C# 計算屬性，若 Repository 誤用它，EF 會直接擲出無法翻譯的例外
        await using var fixture = new SqliteTestDatabase();
        fixture.Db.WorkOrders.AddRange(
            Wo("WO-PLANNED", WorkOrderStatus.Planned),
            Wo("WO-PROGRESS", WorkOrderStatus.InProgress),
            Wo("WO-COMPLETED", WorkOrderStatus.Completed),
            Wo("WO-CANCELLED", WorkOrderStatus.Cancelled));
        await fixture.Db.SaveChangesAsync();

        var repo = new WorkOrderRepository(fixture.CreateContext());
        var result = await repo.GetOpenWorkOrdersByDueDateAsync(Today.AddDays(-30), Today.AddDays(30));

        Assert.Equal(["WO-PLANNED", "WO-PROGRESS"], result.Select(w => w.WorkOrderNo).Order());
    }

    [Fact]
    public async Task 工單查詢會過濾交期區間()
    {
        await using var fixture = new SqliteTestDatabase();
        fixture.Db.WorkOrders.AddRange(
            Wo("WO-BEFORE", WorkOrderStatus.Released, Today.AddDays(-5)),
            Wo("WO-INSIDE", WorkOrderStatus.Released, Today.AddDays(1)),
            Wo("WO-AFTER", WorkOrderStatus.Released, Today.AddDays(30)));
        await fixture.Db.SaveChangesAsync();

        var repo = new WorkOrderRepository(fixture.CreateContext());
        var result = await repo.GetOpenWorkOrdersByDueDateAsync(Today, Today.AddDays(7));

        Assert.Equal("WO-INSIDE", Assert.Single(result).WorkOrderNo);
    }

    [Fact]
    public async Task 採購單查詢只回未結案且能依供應商與料號篩選()
    {
        await using var fixture = new SqliteTestDatabase();
        fixture.Db.PurchaseOrders.AddRange(
            Po("PO-OPEN", "SUP-008", "PANEL-01", PurchaseOrderStatus.Open),
            Po("PO-PARTIAL", "SUP-021", "SCREW-05", PurchaseOrderStatus.PartiallyReceived),
            Po("PO-DONE", "SUP-008", "PANEL-01", PurchaseOrderStatus.Received));
        await fixture.Db.SaveChangesAsync();

        var repo = new PurchaseOrderRepository(fixture.CreateContext());

        Assert.Equal(["PO-OPEN", "PO-PARTIAL"], (await repo.GetOpenAsync()).Select(p => p.PoNo).Order());
        Assert.Equal("PO-OPEN", Assert.Single(await repo.GetOpenAsync(supplierCode: "SUP-008")).PoNo);
        Assert.Equal("PO-PARTIAL", Assert.Single(await repo.GetOpenAsync(itemCode: "SCREW-05")).PoNo);
    }

    [Fact]
    public async Task 關鍵字搜尋會跳脫萬用字元_搜尋百分號不會撈出全部料件()
    {
        await using var fixture = new SqliteTestDatabase();
        fixture.Db.Items.AddRange(
            new Item { ItemCode = "TV-100", ItemName = "液晶電視", ItemType = ItemType.FinishedGood, Unit = "台" },
            new Item { ItemCode = "PANEL-01", ItemName = "面板", ItemType = ItemType.RawMaterial, Unit = "片" },
            new Item { ItemCode = "DISC-50%", ItemName = "促銷品", ItemType = ItemType.FinishedGood, Unit = "個" });
        await fixture.Db.SaveChangesAsync();

        var repo = new ItemRepository(fixture.CreateContext());

        // 若沒跳脫，LIKE '%%%' 會匹配全部三筆
        Assert.Equal("DISC-50%", Assert.Single(await repo.SearchByKeywordAsync("%")).ItemCode);
        Assert.Equal("TV-100", Assert.Single(await repo.SearchByKeywordAsync("TV")).ItemCode);
        Assert.Equal("PANEL-01", Assert.Single(await repo.SearchByKeywordAsync("面板")).ItemCode);
    }

    [Fact]
    public async Task 小數數量存入SQLite再讀出不會失去精度()
    {
        // SQLite 把 decimal 存成 TEXT，這條測試釘住「讀寫無損」這個前提
        await using var fixture = new SqliteTestDatabase();
        fixture.Db.InventoryBalances.Add(new()
        {
            ItemCode = "PANEL-01", OnHandQty = 123.456m, ReservedQty = 0.004m
        });
        await fixture.Db.SaveChangesAsync();

        var repo = new InventoryRepository(fixture.CreateContext());
        var balance = await repo.GetBalanceAsync("PANEL-01");

        Assert.NotNull(balance);
        Assert.Equal(123.456m, balance.OnHandQty);
        Assert.Equal(123.452m, balance.AvailableQty);
    }

    [Fact]
    public async Task 查無料件時回傳null而不是擲例外()
    {
        await using var fixture = new SqliteTestDatabase();
        var repo = new ItemRepository(fixture.CreateContext());

        Assert.Null(await repo.GetByCodeAsync("NOT-EXIST"));
        Assert.Empty(await repo.SearchByKeywordAsync("NOT-EXIST"));
        Assert.Empty(await repo.GetByCodesAsync([]));
    }

    private static WorkOrder Wo(string no, WorkOrderStatus status, DateOnly? due = null) => new()
    {
        WorkOrderNo = no,
        ItemCode = "TV-100",
        PlannedQty = 100m,
        DueDate = due ?? Today,
        Status = status
    };

    private static PurchaseOrder Po(string no, string supplier, string item, PurchaseOrderStatus status) => new()
    {
        PoNo = no,
        SupplierCode = supplier,
        ItemCode = item,
        OrderedQty = 100m,
        ReceivedQty = 0m,
        ExpectedArrivalDate = Today,
        Status = status
    };
}
