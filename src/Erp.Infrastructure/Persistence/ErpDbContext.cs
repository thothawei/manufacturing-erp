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
/// SQLite 的 decimal：欄位型別是 TEXT，但 EF Core 的 SQLite provider 會在連線上註冊
/// ef_compare()、ef_sum() 與 EF_DECIMAL collation，所以透過 EF 下的比較、加總、排序都正確
/// （實測見 SqliteDecimalBehaviourTests）。
///
/// 真正要留意的是：這些函式只存在於 EF Core 開的連線。用 sqlite3 CLI、DB browser
/// 或手寫的原生 SQL 查同一個檔案時，TEXT 會退回字典序比較 —— "9" 會大於 "100"。
/// 因此原生 SQL 不要對數量欄位做比較或排序。
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
