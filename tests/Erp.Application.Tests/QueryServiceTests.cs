using Erp.Application.Common;
using Erp.Application.Inventory;
using Erp.Application.Items;
using Erp.Application.Production;
using Erp.Application.Purchasing;
using Erp.Application.Quality;
using Erp.Application.Tests.Fakes;
using Erp.Domain.Production;
using Erp.Domain.Purchasing;
using Erp.Domain.Quality;

namespace Erp.Application.Tests;

public class ItemMasterQueryServiceTests
{
    private static ItemMasterQueryService CreateService() =>
        new(new InMemoryItemRepository(TestData.Items));

    [Fact]
    public async Task 可用料號或品名關鍵字搜尋()
    {
        var service = CreateService();

        Assert.Equal("TV-100", Assert.Single(await service.SearchByKeywordAsync("TV-100")).ItemCode);
        Assert.Equal("PANEL-01", Assert.Single(await service.SearchByKeywordAsync("面板")).ItemCode);
    }

    [Fact]
    public async Task 空白關鍵字回傳空集合而不是全部料件()
    {
        var service = CreateService();

        Assert.Empty(await service.SearchByKeywordAsync("   "));
    }

    [Fact]
    public async Task 查無相符料件時回傳空集合()
    {
        Assert.Empty(await CreateService().SearchByKeywordAsync("不存在的料號"));
    }
}

public class InventoryQueryServiceTests
{
    private static readonly DateOnly Today = new(2026, 9, 10);

    [Fact]
    public async Task 回傳帳上_保留_可用三個數字並標註查詢日期()
    {
        var service = new InventoryQueryService(
            new InMemoryItemRepository(TestData.Items),
            new InMemoryInventoryRepository([TestData.Balance("PANEL-01", 100m, 60m)]),
            new FakeClock(Today));

        var status = await service.GetStockAsync("PANEL-01");

        Assert.Equal(100m, status.OnHandQty);
        Assert.Equal(60m, status.ReservedQty);
        Assert.Equal(40m, status.AvailableQty);
        Assert.Equal("片", status.Unit);
        Assert.Equal(Today, status.AsOf);
    }

    [Fact]
    public async Task 沒有庫存紀錄的料件回傳零而不是查無資料()
    {
        var service = new InventoryQueryService(
            new InMemoryItemRepository(TestData.Items),
            new InMemoryInventoryRepository([]),
            new FakeClock(Today));

        var status = await service.GetStockAsync("PANEL-01");

        Assert.Equal(0m, status.OnHandQty);
        Assert.Equal(0m, status.AvailableQty);
    }

    [Fact]
    public async Task 料號不存在時擲出查無資料例外()
    {
        var service = new InventoryQueryService(
            new InMemoryItemRepository(TestData.Items),
            new InMemoryInventoryRepository([]),
            new FakeClock(Today));

        await Assert.ThrowsAsync<EntityNotFoundException>(() => service.GetStockAsync("NOT-EXIST"));
    }
}

public class WorkOrderProgressServiceTests
{
    [Fact]
    public async Task 途程站別依站號排序回傳()
    {
        var wo = new WorkOrder
        {
            WorkOrderNo = "WO-01",
            ItemCode = "TV-100",
            PlannedQty = 100m,
            DueDate = new DateOnly(2026, 9, 15),
            Status = WorkOrderStatus.InProgress,
            MaterialIssueStatus = "已全數發料"
        };
        var steps = new RoutingStep[]
        {
            new() { WorkOrderNo = "WO-01", StepNo = 20, OperationName = "組裝", PlannedQty = 100m,
                    CompletedQty = 40m, Status = RoutingStepStatus.InProgress },
            new() { WorkOrderNo = "WO-01", StepNo = 10, OperationName = "裁切", PlannedQty = 100m,
                    CompletedQty = 100m, Status = RoutingStepStatus.Completed },
        };

        var service = new WorkOrderProgressService(new InMemoryWorkOrderRepository([wo], steps));

        var progress = await service.GetProgressAsync("WO-01");

        Assert.Equal([10, 20], progress.RoutingSteps.Select(s => s.StepNo));
        Assert.Equal("已全數發料", progress.MaterialIssueStatus);
    }

    [Fact]
    public async Task 工單不存在時擲出查無資料例外()
    {
        var service = new WorkOrderProgressService(new InMemoryWorkOrderRepository([]));

        await Assert.ThrowsAsync<EntityNotFoundException>(() => service.GetProgressAsync("WO-NONE"));
    }
}

public class PurchasingQueryServiceTests
{
    private static readonly PurchaseOrder[] Orders =
    [
        new() { PoNo = "PO-002", SupplierCode = "SUP-008", ItemCode = "PANEL-01", OrderedQty = 100m,
                ReceivedQty = 0m, ExpectedArrivalDate = new DateOnly(2026, 9, 20), Status = PurchaseOrderStatus.Open },
        new() { PoNo = "PO-001", SupplierCode = "SUP-021", ItemCode = "SCREW-05", OrderedQty = 1000m,
                ReceivedQty = 200m, ExpectedArrivalDate = new DateOnly(2026, 9, 12),
                Status = PurchaseOrderStatus.PartiallyReceived },
        new() { PoNo = "PO-003", SupplierCode = "SUP-008", ItemCode = "PANEL-01", OrderedQty = 50m,
                ReceivedQty = 50m, ExpectedArrivalDate = new DateOnly(2026, 9, 1), Status = PurchaseOrderStatus.Received },
    ];

    [Fact]
    public async Task 只回傳未結案採購單並依預計到貨日排序()
    {
        var service = new PurchasingQueryService(new InMemoryPurchaseOrderRepository(Orders));

        var result = await service.GetOpenPurchaseOrdersAsync();

        Assert.Equal(["PO-001", "PO-002"], result.Select(p => p.PoNo));
    }

    [Fact]
    public async Task 可依供應商與料號篩選()
    {
        var service = new PurchasingQueryService(new InMemoryPurchaseOrderRepository(Orders));

        Assert.Equal("PO-002", Assert.Single(await service.GetOpenPurchaseOrdersAsync(supplierCode: "SUP-008")).PoNo);
        Assert.Equal("PO-001", Assert.Single(await service.GetOpenPurchaseOrdersAsync(itemCode: "SCREW-05")).PoNo);
    }
}

public class QualityInspectionQueryServiceTests
{
    private static readonly QualityInspection[] Inspections =
    [
        new() { InspectionNo = "QC-01", WorkOrderNo = "WO-01", ItemCode = "TV-100",
                InspectedAt = new DateOnly(2026, 9, 8), InspectedQty = 50m, PassedQty = 48m, FailReason = "外觀刮傷" },
        new() { InspectionNo = "QC-02", WorkOrderNo = "WO-01", ItemCode = "TV-100",
                InspectedAt = new DateOnly(2026, 9, 9), InspectedQty = 50m, PassedQty = 45m, FailReason = "點亮異常" },
        new() { InspectionNo = "QC-03", WorkOrderNo = "WO-02", ItemCode = "TV-100",
                InspectedAt = new DateOnly(2026, 9, 9), InspectedQty = 20m, PassedQty = 20m },
    ];

    private static QualityInspectionQueryService CreateService() =>
        new(new InMemoryQualityInspectionRepository(Inspections));

    [Fact]
    public async Task 同一工單的多次檢驗會由後端彙總_不交給LLM自己加總()
    {
        var summary = Assert.Single(await CreateService().GetSummaryAsync(workOrderNo: "WO-01"));

        Assert.Equal(100m, summary.InspectedQty);
        Assert.Equal(93m, summary.PassedQty);
        Assert.Equal(7m, summary.FailedQty);
        Assert.Equal("外觀刮傷、點亮異常", summary.FailReasonSummary);
    }

    [Fact]
    public async Task 全數合格時不良原因為空()
    {
        var summary = Assert.Single(await CreateService().GetSummaryAsync(workOrderNo: "WO-02"));

        Assert.Equal(0m, summary.FailedQty);
        Assert.Null(summary.FailReasonSummary);
    }

    [Fact]
    public async Task 可依日期區間篩選()
    {
        var result = await CreateService().GetSummaryAsync(
            from: new DateOnly(2026, 9, 9), to: new DateOnly(2026, 9, 9));

        Assert.Equal(["WO-01", "WO-02"], result.Select(s => s.WorkOrderNo));
        Assert.Equal(50m, result.First(s => s.WorkOrderNo == "WO-01").InspectedQty); // 只含 9/9 那筆
    }
}
