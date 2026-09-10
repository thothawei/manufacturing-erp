using Erp.Application.Bom;
using Erp.Application.Production;
using Erp.Application.Tests.Fakes;
using Erp.Domain.Inventory;
using Erp.Domain.Production;

namespace Erp.Application.Tests;

public class WorkOrderRiskServiceTests
{
    private static readonly DateOnly Today = new(2026, 9, 10); // 星期四

    private static WorkOrderRiskService CreateService(
        IEnumerable<WorkOrder> workOrders,
        IEnumerable<RoutingStep> steps,
        IEnumerable<InventoryBalance> balances)
    {
        var itemRepo = new InMemoryItemRepository(TestData.Items, TestData.SupplyInfos);
        var bomService = new BomExplosionService(
            itemRepo, new InMemoryBomRepository(TestData.Bom), new InMemoryInventoryRepository(balances));

        return new WorkOrderRiskService(
            new InMemoryWorkOrderRepository(workOrders, steps), itemRepo, bomService, new FakeClock(Today));
    }

    private static WorkOrder Wo(string no, DateOnly due, decimal qty = 100m,
        WorkOrderStatus status = WorkOrderStatus.InProgress) =>
        new() { WorkOrderNo = no, ItemCode = "TV-100", PlannedQty = qty, DueDate = due, Status = status };

    [Fact]
    public async Task 缺料時延遲天數等於補料前置期超出剩餘工作天的部分()
    {
        // 交期 9/15，距今 5 天；PANEL-01 前置期 5 天 → 不會延遲
        // 但把交期改成 9/13（剩 3 天），5 - 3 = 2 天延遲
        var service = CreateService(
            [Wo("WO-01", new DateOnly(2026, 9, 13))],
            [],
            [TestData.Balance("PANEL-01", 80m), TestData.Balance("SCREW-05", 100_000m)]);

        var risks = await service.GetAtRiskWorkOrdersAsync(
            new DateOnly(2026, 9, 7), new DateOnly(2026, 9, 20));

        var risk = Assert.Single(risks);
        Assert.Equal("WO-01", risk.WorkOrderNo);
        Assert.Equal(2, risk.DelayDays);
        Assert.Contains("PANEL-01 短少 120 件", risk.RiskReason); // 需要 200 片、可用 80 片
    }

    [Fact]
    public async Task 缺料但前置期趕得上時仍列為風險_但延遲天數為零()
    {
        var service = CreateService(
            [Wo("WO-02", new DateOnly(2026, 9, 20))], // 距今 10 天，前置期 5 天
            [],
            [TestData.Balance("PANEL-01", 80m), TestData.Balance("SCREW-05", 100_000m)]);

        var risks = await service.GetAtRiskWorkOrdersAsync(
            new DateOnly(2026, 9, 7), new DateOnly(2026, 9, 30));

        var risk = Assert.Single(risks);
        Assert.Equal(0, risk.DelayDays);
        Assert.Contains("缺料", risk.RiskReason);
    }

    [Fact]
    public async Task 已逾交期未完工時延遲天數等於逾期天數()
    {
        var service = CreateService(
            [Wo("WO-03", new DateOnly(2026, 9, 7))], // 逾期 3 天
            [],
            [TestData.Balance("PANEL-01", 100_000m), TestData.Balance("SCREW-05", 100_000m)]);

        var risks = await service.GetAtRiskWorkOrdersAsync(
            new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30));

        var risk = Assert.Single(risks);
        Assert.Equal(3, risk.DelayDays);
        Assert.Contains("已逾交期 3 天", risk.RiskReason);
    }

    [Fact]
    public async Task 剩餘產量以途程最後一站計算_已做完的工單不算風險()
    {
        var steps = new RoutingStep[]
        {
            new() { WorkOrderNo = "WO-04", StepNo = 10, OperationName = "裁切", PlannedQty = 100m,
                    CompletedQty = 100m, Status = RoutingStepStatus.Completed },
            new() { WorkOrderNo = "WO-04", StepNo = 20, OperationName = "組裝", PlannedQty = 100m,
                    CompletedQty = 100m, Status = RoutingStepStatus.Completed },
        };

        var service = CreateService([Wo("WO-04", new DateOnly(2026, 9, 7))], steps, []);

        var risks = await service.GetAtRiskWorkOrdersAsync(
            new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30));

        Assert.Empty(risks);
    }

    [Fact]
    public async Task 缺料判定只針對剩餘產量_已完工部分不重複要料()
    {
        // 計畫 100 台，已完成 90 台，剩 10 台需要 20 片面板，庫存 20 片剛好夠
        var steps = new RoutingStep[]
        {
            new() { WorkOrderNo = "WO-05", StepNo = 10, OperationName = "組裝", PlannedQty = 100m,
                    CompletedQty = 90m, Status = RoutingStepStatus.InProgress },
        };

        var service = CreateService(
            [Wo("WO-05", new DateOnly(2026, 9, 20))],
            steps,
            [TestData.Balance("PANEL-01", 20m), TestData.Balance("SCREW-05", 100_000m)]);

        var risks = await service.GetAtRiskWorkOrdersAsync(
            new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30));

        Assert.Empty(risks);
    }

    [Fact]
    public async Task 未指定區間時預設查詢本週()
    {
        // Today = 2026-09-10（週四），本週為 09-07(一) ~ 09-13(日)
        var service = CreateService(
        [
            Wo("WO-IN", new DateOnly(2026, 9, 11)),
            Wo("WO-OUT", new DateOnly(2026, 9, 21)),
        ], [], []);

        var risks = await service.GetAtRiskWorkOrdersAsync();

        Assert.Equal("WO-IN", Assert.Single(risks).WorkOrderNo);
    }

    [Fact]
    public async Task 已完工或取消的工單不納入風險清單()
    {
        var service = CreateService(
        [
            Wo("WO-DONE", new DateOnly(2026, 9, 7), status: WorkOrderStatus.Completed),
            Wo("WO-CANCEL", new DateOnly(2026, 9, 7), status: WorkOrderStatus.Cancelled),
        ], [], []);

        var risks = await service.GetAtRiskWorkOrdersAsync(
            new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30));

        Assert.Empty(risks);
    }
}
