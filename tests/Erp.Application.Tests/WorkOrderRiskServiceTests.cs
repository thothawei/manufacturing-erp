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
    public async Task 未指定區間時查到本週日為止()
    {
        // Today = 2026-09-10（週四），預設查到本週日 09-13
        var service = CreateService(
        [
            Wo("WO-IN", new DateOnly(2026, 9, 11)),
            Wo("WO-OUT", new DateOnly(2026, 9, 21)),
        ], [], []);

        var risks = await service.GetAtRiskWorkOrdersAsync();

        Assert.Equal("WO-IN", Assert.Single(risks).WorkOrderNo);
    }

    [Fact]
    public async Task 未指定起始日時上週就逾期的工單仍然看得到()
    {
        // 這條釘的是一個實際的漏看：起點若取本週一（09-07），
        // 08-28 就逾期、至今未結案的工單會被整批濾掉 ——
        // 而逾期正是兩種風險來源中最急的那一種。
        var service = CreateService(
            [Wo("WO-OVERDUE", new DateOnly(2026, 8, 28))],
            [],
            [TestData.Balance("PANEL-01", 100_000m), TestData.Balance("SCREW-05", 100_000m)]);

        var risks = await service.GetAtRiskWorkOrdersAsync();

        var risk = Assert.Single(risks);
        Assert.Equal("WO-OVERDUE", risk.WorkOrderNo);
        Assert.Equal(13, risk.DelayDays);
    }

    [Fact]
    public async Task 指定視窗天數時查的是今天起算的那幾天()
    {
        // windowDays = 14 → 09-10 ~ 09-23，所以 09-21 那張會進來、09-30 那張不會
        var service = CreateService(
        [
            Wo("WO-IN", new DateOnly(2026, 9, 21)),
            Wo("WO-OUT", new DateOnly(2026, 9, 30)),
        ], [], []);

        var risks = await service.GetAtRiskWorkOrdersAsync(windowDays: 14);

        Assert.Equal("WO-IN", Assert.Single(risks).WorkOrderNo);
    }

    [Fact]
    public async Task 明確指定的日期區間優先於視窗天數()
    {
        // 兩種都給時以日期區間為準：它是更精確的表達。
        // 若 windowDays 反過來覆蓋日期區間，WO-OUT 就會被濾掉。
        var service = CreateService(
            [Wo("WO-OUT", new DateOnly(2026, 9, 30))], [], []);

        var risks = await service.GetAtRiskWorkOrdersAsync(
            new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), windowDays: 3);

        Assert.Equal("WO-OUT", Assert.Single(risks).WorkOrderNo);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(366)]
    public async Task 視窗天數不合理時擲出參數例外(int windowDays)
    {
        var service = CreateService([], [], []);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => service.GetAtRiskWorkOrdersAsync(windowDays: windowDays));
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
