using Erp.Domain.Bom;
using Erp.Domain.Inventory;
using Erp.Domain.Items;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Erp.Infrastructure.Persistence.Configurations;

public sealed class ItemConfiguration : IEntityTypeConfiguration<Item>
{
    public void Configure(EntityTypeBuilder<Item> builder)
    {
        builder.ToTable("items");
        builder.HasKey(i => i.ItemCode);
        builder.Property(i => i.ItemCode).HasMaxLength(50);
        builder.Property(i => i.ItemName).HasMaxLength(200).IsRequired();
        builder.Property(i => i.Unit).HasMaxLength(20).IsRequired();
        builder.Property(i => i.ItemType).HasConversion<string>().HasMaxLength(20);
        builder.HasIndex(i => i.ItemName);
    }
}

public sealed class ItemSupplyInfoConfiguration : IEntityTypeConfiguration<ItemSupplyInfo>
{
    public void Configure(EntityTypeBuilder<ItemSupplyInfo> builder)
    {
        builder.ToTable("item_supply_infos");
        builder.HasKey(s => s.ItemCode);
        builder.Property(s => s.ItemCode).HasMaxLength(50);
        builder.Property(s => s.SupplierCode).HasMaxLength(50).IsRequired();
    }
}

public sealed class InventoryBalanceConfiguration : IEntityTypeConfiguration<InventoryBalance>
{
    public void Configure(EntityTypeBuilder<InventoryBalance> builder)
    {
        builder.ToTable("inventory_balances");
        builder.HasKey(b => b.ItemCode);
        builder.Property(b => b.ItemCode).HasMaxLength(50);

        // 計算屬性，不落庫
        builder.Ignore(b => b.AvailableQty);
    }
}

public sealed class BomLineConfiguration : IEntityTypeConfiguration<BomLine>
{
    public void Configure(EntityTypeBuilder<BomLine> builder)
    {
        builder.ToTable("bom_lines");

        // BOM 沒有自然單鍵，以「父件 + 子件 + 版本」唯一識別
        builder.HasKey(l => new { l.ParentItemCode, l.ComponentItemCode, l.BomVersion });
        builder.Property(l => l.ParentItemCode).HasMaxLength(50);
        builder.Property(l => l.ComponentItemCode).HasMaxLength(50);
        builder.Property(l => l.BomVersion).HasMaxLength(20);

        // 展開時是「給父件、找子件」，索引方向要跟查詢一致
        builder.HasIndex(l => l.ParentItemCode);
    }
}
