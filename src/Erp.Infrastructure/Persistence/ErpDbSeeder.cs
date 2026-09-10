using Erp.Application.Common;
using Erp.Domain.Bom;
using Erp.Domain.Inventory;
using Erp.Domain.Items;
using Erp.Domain.Production;
using Erp.Domain.Purchasing;
using Erp.Domain.Quality;
using Microsoft.EntityFrameworkCore;

namespace Erp.Infrastructure.Persistence;

/// 展示用種子資料。
///
/// 所有日期都以「執行當天」為基準相對產生，資料不會隨時間過期，
/// 隨時重建資料庫都能重現同一組情境：
///
///   WO-xxx-01（TV-100 100 台，3 天後到期）→ 面板可用 80 片、需要 200 片
///       → 缺 120 片，補料前置期 5 天但只剩 3 天 → 延遲 2 天
///   MRP 全域試算 → 面板淨缺 130 片，訂購倍量 50 → 建議下單 150 片
///
/// 這正是 docs/ai-assistant-module-plan-v2.md 第 5 節範例 2 的情境。
public static class ErpDbSeeder
{
    public static async Task SeedAsync(ErpDbContext db, IClock clock, CancellationToken ct = default)
    {
        try
        {
            await SeedCoreAsync(db, clock, ct);
        }
        catch (DbUpdateException)
        {
            // 失敗的批次還掛在 ChangeTracker 上，不清掉的話接下來的查詢會看到它們
            db.ChangeTracker.Clear();

            if (!await db.Items.AnyAsync(ct))
            {
                throw;   // 資料真的沒進去，那是別的問題，不要吞掉
            }

            // 另一個執行緒或實例正在灌同一份資料，兩邊都通過了「是否已有資料」的檢查。
            // 檢查與插入之間本來就有空窗，SQLite 的交易也擋不住（deferred 交易的讀取不取寫鎖），
            // 所以改成讓衝突發生、確認資料確實已經在了，就當作成功。
            //
            // 這不只是測試問題：多個 API 實例同時啟動時，生產環境會遇到一模一樣的競態。
            // WebApplicationFactory 會建立 host 不只一次，測試冷啟動時兩次 seeding 真正重疊，
            // 熱身之後第一次太快完成、第二次就只會看到資料而跳過 —— 這就是它表現成
            // 「每個 build 組態的第一次執行才失敗」的原因。
        }
    }

    private static async Task SeedCoreAsync(ErpDbContext db, IClock clock, CancellationToken ct)
    {
        if (await db.Items.AnyAsync(ct))
        {
            return; // 已有資料就不重複灌，可安全重複呼叫
        }

        var today = clock.Today;
        var stamp = today.ToString("yyyyMMdd");

        db.Items.AddRange(
            new Item { ItemCode = "TV-100", ItemName = "液晶電視 55 吋", ItemType = ItemType.FinishedGood, Unit = "台" },
            new Item { ItemCode = "MON-200", ItemName = "顯示器 27 吋", ItemType = ItemType.FinishedGood, Unit = "台" },
            new Item { ItemCode = "CHASSIS-02", ItemName = "機殼組件", ItemType = ItemType.SemiFinished, Unit = "組" },
            new Item { ItemCode = "PANEL-01", ItemName = "面板", ItemType = ItemType.RawMaterial, Unit = "片" },
            new Item { ItemCode = "SCREW-05", ItemName = "螺絲 M3", ItemType = ItemType.RawMaterial, Unit = "支" },
            new Item { ItemCode = "CABLE-07", ItemName = "訊號線", ItemType = ItemType.RawMaterial, Unit = "條" });

        db.ItemSupplyInfos.AddRange(
            new ItemSupplyInfo { ItemCode = "PANEL-01", SupplierCode = "SUP-008", LeadTimeDays = 5, OrderMultiple = 50m },
            new ItemSupplyInfo { ItemCode = "SCREW-05", SupplierCode = "SUP-021", LeadTimeDays = 2, MinOrderQty = 1_000m },
            new ItemSupplyInfo { ItemCode = "CABLE-07", SupplierCode = "SUP-015", LeadTimeDays = 3 });

        // TV-100 ├ PANEL-01 ×2  ├ CHASSIS-02 ×3 ─ SCREW-05 ×4  └ CABLE-07 ×1
        // 螺絲對成品的用量是 3×4=12，這組結構就是多階累乘的示範
        db.BomLines.AddRange(
            new BomLine { ParentItemCode = "TV-100", ComponentItemCode = "PANEL-01", QtyPer = 2m, BomVersion = "v3" },
            new BomLine { ParentItemCode = "TV-100", ComponentItemCode = "CHASSIS-02", QtyPer = 3m, BomVersion = "v3" },
            new BomLine { ParentItemCode = "TV-100", ComponentItemCode = "CABLE-07", QtyPer = 1m, BomVersion = "v3" },
            new BomLine { ParentItemCode = "CHASSIS-02", ComponentItemCode = "SCREW-05", QtyPer = 4m, BomVersion = "v2" },
            new BomLine { ParentItemCode = "MON-200", ComponentItemCode = "PANEL-01", QtyPer = 1m, BomVersion = "v1" },
            new BomLine { ParentItemCode = "MON-200", ComponentItemCode = "CABLE-07", QtyPer = 2m, BomVersion = "v1" });

        // 面板帳上 100 片但保留了 20 片，可用只有 80 片 —— 這個差距就是「可用 vs 帳上」的示範
        db.InventoryBalances.AddRange(
            new InventoryBalance { ItemCode = "PANEL-01", OnHandQty = 100m, ReservedQty = 20m },
            new InventoryBalance { ItemCode = "SCREW-05", OnHandQty = 6_000m, ReservedQty = 0m },
            new InventoryBalance { ItemCode = "CABLE-07", OnHandQty = 500m, ReservedQty = 0m },
            new InventoryBalance { ItemCode = "CHASSIS-02", OnHandQty = 40m, ReservedQty = 0m },
            new InventoryBalance { ItemCode = "TV-100", OnHandQty = 12m, ReservedQty = 0m });

        var shortageWo = $"WO-{stamp}-01";  // 缺料風險
        var overdueWo = $"WO-{stamp}-02";   // 逾期風險
        var futureWo = $"WO-{stamp}-03";    // 遠期，不進本週也不進 30 天試算
        var doneWo = $"WO-{stamp}-04";      // 已完工，提供品管歷史

        db.WorkOrders.AddRange(
            new WorkOrder
            {
                WorkOrderNo = shortageWo,
                ItemCode = "TV-100",
                PlannedQty = 100m,
                DueDate = today.AddDays(3),
                Status = WorkOrderStatus.Released,
                MaterialIssueStatus = "未發料"
            },
            new WorkOrder
            {
                WorkOrderNo = overdueWo,
                ItemCode = "MON-200",
                PlannedQty = 30m,
                DueDate = today.AddDays(-2),
                Status = WorkOrderStatus.InProgress,
                MaterialIssueStatus = "已全數發料"
            },
            new WorkOrder
            {
                WorkOrderNo = futureWo,
                ItemCode = "MON-200",
                PlannedQty = 30m,
                DueDate = today.AddDays(45),
                Status = WorkOrderStatus.Planned,
                MaterialIssueStatus = "未發料"
            },
            new WorkOrder
            {
                WorkOrderNo = doneWo,
                ItemCode = "TV-100",
                PlannedQty = 20m,
                DueDate = today.AddDays(-10),
                Status = WorkOrderStatus.Completed,
                MaterialIssueStatus = "已全數發料"
            });

        db.RoutingSteps.AddRange(
            new RoutingStep { WorkOrderNo = shortageWo, StepNo = 10, OperationName = "面板貼合", PlannedQty = 100m, CompletedQty = 0m, Status = RoutingStepStatus.NotStarted },
            new RoutingStep { WorkOrderNo = shortageWo, StepNo = 20, OperationName = "機殼組裝", PlannedQty = 100m, CompletedQty = 0m, Status = RoutingStepStatus.NotStarted },
            new RoutingStep { WorkOrderNo = shortageWo, StepNo = 30, OperationName = "測試包裝", PlannedQty = 100m, CompletedQty = 0m, Status = RoutingStepStatus.NotStarted },

            // 逾期工單已做 20 台，剩 10 台 —— 缺料判定只會針對這剩下的 10 台
            new RoutingStep { WorkOrderNo = overdueWo, StepNo = 10, OperationName = "面板貼合", PlannedQty = 30m, CompletedQty = 30m, Status = RoutingStepStatus.Completed },
            new RoutingStep { WorkOrderNo = overdueWo, StepNo = 20, OperationName = "測試包裝", PlannedQty = 30m, CompletedQty = 20m, Status = RoutingStepStatus.InProgress },

            new RoutingStep { WorkOrderNo = doneWo, StepNo = 10, OperationName = "面板貼合", PlannedQty = 20m, CompletedQty = 20m, Status = RoutingStepStatus.Completed },
            new RoutingStep { WorkOrderNo = doneWo, StepNo = 20, OperationName = "測試包裝", PlannedQty = 20m, CompletedQty = 20m, Status = RoutingStepStatus.Completed });

        db.PurchaseOrders.AddRange(
            // 面板已下單 30 片，但 10 天後才到、趕不上 3 天後的需求 → MRP 不能把它算成供給
            new PurchaseOrder
            {
                PoNo = $"PO-{stamp}-001",
                SupplierCode = "SUP-008",
                ItemCode = "PANEL-01",
                OrderedQty = 30m,
                ReceivedQty = 0m,
                ExpectedArrivalDate = today.AddDays(10),
                Status = PurchaseOrderStatus.Open
            },
            new PurchaseOrder
            {
                PoNo = $"PO-{stamp}-002",
                SupplierCode = "SUP-021",
                ItemCode = "SCREW-05",
                OrderedQty = 1_000m,
                ReceivedQty = 400m,
                ExpectedArrivalDate = today.AddDays(2),
                Status = PurchaseOrderStatus.PartiallyReceived
            },
            new PurchaseOrder
            {
                PoNo = $"PO-{stamp}-003",
                SupplierCode = "SUP-015",
                ItemCode = "CABLE-07",
                OrderedQty = 100m,
                ReceivedQty = 100m,
                ExpectedArrivalDate = today.AddDays(-5),
                Status = PurchaseOrderStatus.Received
            });

        db.QualityInspections.AddRange(
            new QualityInspection
            {
                InspectionNo = $"QC-{stamp}-001",
                WorkOrderNo = overdueWo,
                ItemCode = "MON-200",
                InspectedAt = today.AddDays(-3),
                InspectedQty = 10m,
                PassedQty = 9m,
                FailReason = "外觀刮傷"
            },
            new QualityInspection
            {
                InspectionNo = $"QC-{stamp}-002",
                WorkOrderNo = overdueWo,
                ItemCode = "MON-200",
                InspectedAt = today.AddDays(-1),
                InspectedQty = 10m,
                PassedQty = 8m,
                FailReason = "亮點超標"
            },
            new QualityInspection
            {
                InspectionNo = $"QC-{stamp}-003",
                WorkOrderNo = doneWo,
                ItemCode = "TV-100",
                InspectedAt = today.AddDays(-9),
                InspectedQty = 20m,
                PassedQty = 20m
            });

        await db.SaveChangesAsync(ct);
    }
}
