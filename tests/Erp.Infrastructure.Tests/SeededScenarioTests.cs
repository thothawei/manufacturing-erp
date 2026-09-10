using Erp.Application.Bom;
using Erp.Application.Mrp;
using Erp.Application.Production;
using Erp.Application.Quality;
using Erp.Infrastructure.Persistence;
using Erp.Infrastructure.Persistence.Repositories;

namespace Erp.Infrastructure.Tests;

/// 用真的 EF Core 路徑 + 種子資料跑完整服務鏈。
/// 這裡釘住的數字就是 docs/ai-assistant-module-plan-v2.md 第 5 節範例 2 的情境，
/// 改動種子資料而沒同步更新文件時，這組測試會先紅。
public class SeededScenarioTests : IAsyncLifetime
{
    private static readonly DateOnly Today = new(2026, 9, 10);
    private static readonly string Stamp = Today.ToString("yyyyMMdd");

    private SqliteTestDatabase _fixture = null!;
    private TestClock _clock = null!;

    public async Task InitializeAsync()
    {
        _fixture = new SqliteTestDatabase();
        _clock = new TestClock(Today);
        await ErpDbSeeder.SeedAsync(_fixture.Db, _clock);
    }

    public async Task DisposeAsync() => await _fixture.DisposeAsync();

    private BomExplosionService CreateBomService()
    {
        var db = _fixture.CreateContext();
        return new BomExplosionService(new ItemRepository(db), new BomRepository(db), new InventoryRepository(db));
    }

    [Fact]
    public async Task 種子資料可重複執行不會灌兩份()
    {
        await ErpDbSeeder.SeedAsync(_fixture.Db, _clock);
        await ErpDbSeeder.SeedAsync(_fixture.Db, _clock);

        Assert.Equal(6, _fixture.CreateContext().Items.Count());
        Assert.Equal(4, _fixture.CreateContext().WorkOrders.Count());
    }

    [Fact]
    public async Task 多階展開_螺絲對成品用量為十二支()
    {
        var leaves = await CreateBomService().ExplodeToLeavesAsync("TV-100");

        // CHASSIS-02 ×3，每組 4 支螺絲 → 對成品 12 支
        Assert.Equal(12m, leaves.Single(l => l.ComponentCode == "SCREW-05").RequiredPerFinishedUnit);
        Assert.Equal(2m, leaves.Single(l => l.ComponentCode == "PANEL-01").RequiredPerFinishedUnit);
        Assert.Equal(1m, leaves.Single(l => l.ComponentCode == "CABLE-07").RequiredPerFinishedUnit);
    }

    [Fact]
    public async Task 可製造量以可用庫存計算_面板帳上一百片但只做得出四十台()
    {
        // 面板帳上 100 片、保留 20 片 → 可用 80 片 → 80 / 2 = 40 台
        // 若誤用帳上庫存會算成 50 台
        var result = await CreateBomService().CalculateMaxBuildableAsync("TV-100");

        Assert.Equal(40m, result.MaxBuildableQty);
        Assert.Equal("available", result.Basis);
        Assert.Equal("v3", result.BomVersion);
    }

    [Fact]
    public async Task 風險工單_缺料的那張延遲兩天且短少一百二十片面板()
    {
        var db = _fixture.CreateContext();
        var service = new WorkOrderRiskService(
            new WorkOrderRepository(db), new ItemRepository(db), CreateBomService(), _clock);

        var risks = await service.GetAtRiskWorkOrdersAsync(Today.AddDays(-7), Today.AddDays(7));

        var shortage = risks.Single(r => r.WorkOrderNo == $"WO-{Stamp}-01");
        Assert.Equal(2, shortage.DelayDays);                       // 前置期 5 天 − 剩餘 3 天
        Assert.Contains("PANEL-01 短少 120 件", shortage.RiskReason); // 需要 200 片、可用 80 片

        var overdue = risks.Single(r => r.WorkOrderNo == $"WO-{Stamp}-02");
        Assert.Equal(2, overdue.DelayDays);
        Assert.Contains("已逾交期 2 天", overdue.RiskReason);
        Assert.Contains("尚有 10 個未完工", overdue.RiskReason);     // 30 台已做 20 台
    }

    [Fact]
    public async Task 風險工單_遠期與已完工的工單不會出現在本週清單()
    {
        var db = _fixture.CreateContext();
        var service = new WorkOrderRiskService(
            new WorkOrderRepository(db), new ItemRepository(db), CreateBomService(), _clock);

        var risks = await service.GetAtRiskWorkOrdersAsync(Today.AddDays(-7), Today.AddDays(7));

        Assert.DoesNotContain(risks, r => r.WorkOrderNo == $"WO-{Stamp}-03"); // 45 天後才到期
        Assert.DoesNotContain(risks, r => r.WorkOrderNo == $"WO-{Stamp}-04"); // 已完工
    }

    [Fact]
    public async Task MRP試算_面板淨缺一百三十片且建議下單一百五十片()
    {
        var db = _fixture.CreateContext();
        var service = new MrpCalculationService(
            new WorkOrderRepository(db), new ItemRepository(db), new InventoryRepository(db),
            new PurchaseOrderRepository(db), CreateBomService(), _clock);

        var result = await service.RunShortageAnalysisAsync();

        var panel = result.ShortageItems.Single(s => s.ItemCode == "PANEL-01");
        Assert.Equal(210m, panel.GrossRequirementQty);  // 100 台 TV ×2 + 10 台 MON ×1
        Assert.Equal(80m, panel.AvailableQty);
        Assert.Equal(0m, panel.InTransitQty);            // 已下單的 30 片 10 天後才到，趕不上
        Assert.Equal(130m, panel.NetShortageQty);
        Assert.Equal(150m, panel.SuggestedOrderQty);     // 訂購倍量 50 → 無條件進位
        Assert.Equal("SUP-008", panel.SupplierCode);
        Assert.Equal(5, panel.LeadTimeDays);
    }

    [Fact]
    public async Task MRP試算_庫存充足的料件不會出現在缺料清單()
    {
        var db = _fixture.CreateContext();
        var service = new MrpCalculationService(
            new WorkOrderRepository(db), new ItemRepository(db), new InventoryRepository(db),
            new PurchaseOrderRepository(db), CreateBomService(), _clock);

        var result = await service.RunShortageAnalysisAsync();

        Assert.DoesNotContain(result.ShortageItems, s => s.ItemCode == "SCREW-05");  // 需 1200 支、有 6000 支
        Assert.DoesNotContain(result.ShortageItems, s => s.ItemCode == "CABLE-07");  // 需 120 條、有 500 條
        Assert.Equal("PANEL-01", Assert.Single(result.ShortageItems).ItemCode);
    }

    [Fact]
    public async Task 品管彙總_同一工單的兩次檢驗會合併成一列()
    {
        var service = new QualityInspectionQueryService(
            new QualityInspectionRepository(_fixture.CreateContext()));

        var summary = Assert.Single(await service.GetSummaryAsync(workOrderNo: $"WO-{Stamp}-02"));

        Assert.Equal(20m, summary.InspectedQty);
        Assert.Equal(17m, summary.PassedQty);
        Assert.Equal(3m, summary.FailedQty);
        Assert.Equal("外觀刮傷、亮點超標", summary.FailReasonSummary);
    }
}
