using Erp.Domain.Bom;
using Erp.Domain.Inventory;
using Erp.Domain.Items;
using Erp.Domain.Production;
using Erp.Domain.Purchasing;
using Erp.Domain.Quality;
using Microsoft.EntityFrameworkCore;

namespace Erp.Infrastructure.Persistence;

/// EF Core 資料庫內容。
///
/// SQLite 注意事項：decimal 會存成 TEXT，資料庫層無法正確比較或排序。
/// 因此所有對數量欄位的比較、加總、排序都必須在載入到記憶體之後才做，
/// 查詢條件只用字串、日期與列舉（見各 Repository 實作）。
public sealed class ErpDbContext(DbContextOptions<ErpDbContext> options) : DbContext(options)
{
    public DbSet<Item> Items => Set<Item>();
    public DbSet<ItemSupplyInfo> ItemSupplyInfos => Set<ItemSupplyInfo>();
    public DbSet<InventoryBalance> InventoryBalances => Set<InventoryBalance>();
    public DbSet<BomLine> BomLines => Set<BomLine>();
    public DbSet<WorkOrder> WorkOrders => Set<WorkOrder>();
    public DbSet<RoutingStep> RoutingSteps => Set<RoutingStep>();
    public DbSet<PurchaseOrder> PurchaseOrders => Set<PurchaseOrder>();
    public DbSet<QualityInspection> QualityInspections => Set<QualityInspection>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.ApplyConfigurationsFromAssembly(typeof(ErpDbContext).Assembly);
}
