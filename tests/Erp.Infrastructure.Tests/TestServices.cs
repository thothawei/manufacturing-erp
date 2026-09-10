using Erp.Application.Bom;
using Erp.Application.Common;
using Erp.Application.Inventory;
using Erp.Application.Items;
using Erp.Application.Mrp;
using Erp.Application.Production;
using Erp.Application.Purchasing;
using Erp.Application.Quality;
using Erp.Infrastructure.AI;
using Erp.Infrastructure.Persistence.Repositories;

namespace Erp.Infrastructure.Tests;

internal static class TestServices
{
    /// 用真的 EF Core Repository 組出完整的 ToolDispatcher。
    /// 集中一處，之後新增工具只要改這裡一個地方。
    public static ToolDispatcher CreateDispatcher(SqliteTestDatabase fixture, IClock clock)
    {
        var db = fixture.CreateContext();

        var itemRepository = new ItemRepository(db);
        var inventoryRepository = new InventoryRepository(db);
        var workOrderRepository = new WorkOrderRepository(db);
        var purchaseOrderRepository = new PurchaseOrderRepository(db);

        var bomExplosionService = new BomExplosionService(itemRepository, new BomRepository(db), inventoryRepository);

        return new ToolDispatcher(
            new ItemMasterQueryService(itemRepository),
            new InventoryQueryService(itemRepository, inventoryRepository, clock),
            bomExplosionService,
            new WorkOrderProgressService(workOrderRepository),
            new WorkOrderRiskService(workOrderRepository, itemRepository, bomExplosionService, clock),
            new MrpCalculationService(
                workOrderRepository, itemRepository, inventoryRepository,
                purchaseOrderRepository, bomExplosionService, clock),
            new PurchasingQueryService(purchaseOrderRepository),
            new QualityInspectionQueryService(new QualityInspectionRepository(db)));
    }
}
