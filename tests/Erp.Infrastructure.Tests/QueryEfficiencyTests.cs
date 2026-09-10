using Erp.Application.Bom;
using Erp.Application.Mrp;
using Erp.Domain.Bom;
using Erp.Domain.Items;
using Erp.Domain.Production;
using Erp.Infrastructure.Persistence;
using Erp.Infrastructure.Persistence.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Erp.Infrastructure.Tests;

/// 防止「在迴圈裡逐筆查資料庫」的寫法被改回來。
///
/// 判準是查詢次數如何「隨缺料料號數成長」，不是絕對次數 ——
/// 絕對次數會隨 BOM 結構改變，斷言它只會製造脆弱的測試。
public class QueryEfficiencyTests
{
    private static readonly DateOnly Today = new(2026, 9, 10);

    [Fact]
    public async Task 缺料料號變多時_採購單與補料條件的查詢次數不跟著成長()
    {
        var withTwo = await CountMrpQueriesAsync(componentCount: 2);
        var withFour = await CountMrpQueriesAsync(componentCount: 4);

        // 多出來的 2 個子件，BOM 遞迴本身各需一次查詢確認是否為葉節點 → 允許 +2。
        // 若採購單或補料條件改回在迴圈裡逐筆查，增量會變成 +6，這條就會紅。
        var growth = withFour - withTwo;
        Assert.True(growth <= 2, $"缺料料號從 2 增為 4 時，查詢次數增加了 {growth} 次（允許上限 2 次）");
    }

    private static async Task<int> CountMrpQueriesAsync(int componentCount)
    {
        var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();

        var queryCount = 0;
        ErpDbContext CreateContext() => new(new DbContextOptionsBuilder<ErpDbContext>()
            .UseSqlite(connection)
            .LogTo(line =>
            {
                if (line.Contains("Executed DbCommand"))
                {
                    Interlocked.Increment(ref queryCount);
                }
            })
            .Options);

        await using (var setup = CreateContext())
        {
            await setup.Database.MigrateAsync();

            setup.Items.Add(new Item
            {
                ItemCode = "F1", ItemName = "成品", ItemType = ItemType.FinishedGood, Unit = "台"
            });

            for (var i = 1; i <= componentCount; i++)
            {
                setup.Items.Add(new Item
                {
                    ItemCode = $"R{i}", ItemName = $"原料{i}", ItemType = ItemType.RawMaterial, Unit = "個"
                });
                setup.BomLines.Add(new BomLine
                {
                    ParentItemCode = "F1", ComponentItemCode = $"R{i}", QtyPer = 1m, BomVersion = "v1"
                });
                setup.ItemSupplyInfos.Add(new ItemSupplyInfo
                {
                    ItemCode = $"R{i}", SupplierCode = "SUP-001", LeadTimeDays = 3
                });
            }

            // 完全沒有庫存 → 每個原料都會落入缺料清單
            setup.WorkOrders.Add(new WorkOrder
            {
                WorkOrderNo = "WO-01", ItemCode = "F1", PlannedQty = 10m,
                DueDate = Today.AddDays(5), Status = WorkOrderStatus.Released
            });

            await setup.SaveChangesAsync();
        }

        queryCount = 0;

        await using var db = CreateContext();
        var service = new MrpCalculationService(
            new WorkOrderRepository(db), new ItemRepository(db), new InventoryRepository(db),
            new PurchaseOrderRepository(db),
            new BomExplosionService(new ItemRepository(db), new BomRepository(db), new InventoryRepository(db)),
            new TestClock(Today));

        var result = await service.RunShortageAnalysisAsync();
        Assert.Equal(componentCount, result.ShortageItems.Count); // 確認情境成立，否則計數沒有意義

        await connection.DisposeAsync();
        return queryCount;
    }
}
