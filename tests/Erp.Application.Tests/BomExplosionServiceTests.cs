using Erp.Application.Bom;
using Erp.Application.Common;
using Erp.Application.Tests.Fakes;
using Erp.Domain.Bom;
using Erp.Domain.Inventory;
using Erp.Domain.Items;

namespace Erp.Application.Tests;

public class BomExplosionServiceTests
{
    // 文件第 3.1 節的結構：中間階 CHASSIS-02 用量刻意設為 3（不是 1），
    // 否則「相對父件」與「相對成品」兩種語意會同值，測不出差別。
    //   TV-100 ├── PANEL-01   × 2
    //          └── CHASSIS-02 × 3
    //                  └── SCREW-05 × 4   → 相對成品 = 12
    private static readonly Item[] Items =
    [
        new() { ItemCode = "TV-100", ItemName = "電視機", ItemType = ItemType.FinishedGood, Unit = "台" },
        new() { ItemCode = "PANEL-01", ItemName = "面板", ItemType = ItemType.RawMaterial, Unit = "片" },
        new() { ItemCode = "CHASSIS-02", ItemName = "機殼組件", ItemType = ItemType.SemiFinished, Unit = "組" },
        new() { ItemCode = "SCREW-05", ItemName = "螺絲", ItemType = ItemType.RawMaterial, Unit = "支" },
    ];

    private static readonly BomLine[] Bom =
    [
        new() { ParentItemCode = "TV-100", ComponentItemCode = "PANEL-01", QtyPer = 2m, BomVersion = "v3" },
        new() { ParentItemCode = "TV-100", ComponentItemCode = "CHASSIS-02", QtyPer = 3m, BomVersion = "v3" },
        new() { ParentItemCode = "CHASSIS-02", ComponentItemCode = "SCREW-05", QtyPer = 4m, BomVersion = "v1" },
    ];

    private static BomExplosionService CreateService(IEnumerable<InventoryBalance> balances) =>
        new(new InMemoryItemRepository(Items), new InMemoryBomRepository(Bom), new InMemoryInventoryRepository(balances));

    private static InventoryBalance Balance(string code, decimal onHand, decimal reserved = 0m) =>
        new() { ItemCode = code, OnHandQty = onHand, ReservedQty = reserved };

    [Fact]
    public async Task 多階展開_中間階用量會逐層累乘到最終成品()
    {
        var service = CreateService([]);

        var leaves = await service.ExplodeToLeavesAsync("TV-100");

        Assert.Equal(2, leaves.Count);
        Assert.Equal(2m, leaves.Single(l => l.ComponentCode == "PANEL-01").RequiredPerFinishedUnit);
        // 3（CHASSIS-02 用量）× 4（每組螺絲）= 12，而不是 4
        Assert.Equal(12m, leaves.Single(l => l.ComponentCode == "SCREW-05").RequiredPerFinishedUnit);
    }

    [Fact]
    public async Task 多階展開_不同路徑用到同一原料時需求量會累加()
    {
        var items = Items.Append(
            new Item { ItemCode = "BRACKET-09", ItemName = "支架", ItemType = ItemType.SemiFinished, Unit = "組" });
        var bom = Bom.Concat(
        [
            new BomLine { ParentItemCode = "TV-100", ComponentItemCode = "BRACKET-09", QtyPer = 1m, BomVersion = "v3" },
            new BomLine { ParentItemCode = "BRACKET-09", ComponentItemCode = "SCREW-05", QtyPer = 5m, BomVersion = "v1" },
        ]);

        var service = new BomExplosionService(
            new InMemoryItemRepository(items), new InMemoryBomRepository(bom), new InMemoryInventoryRepository([]));

        var leaves = await service.ExplodeToLeavesAsync("TV-100");

        // 走 CHASSIS-02 那條 12 支，走 BRACKET-09 那條 5 支，合計 17 支
        Assert.Equal(17m, leaves.Single(l => l.ComponentCode == "SCREW-05").RequiredPerFinishedUnit);
    }

    [Fact]
    public async Task 可製造量以可用庫存計算_不使用帳上庫存()
    {
        // PANEL-01 帳上 100 片但保留了 60，可用只剩 40 → 最多 20 台（用帳上算會誤判成 50 台）
        var service = CreateService(
        [
            Balance("PANEL-01", onHand: 100m, reserved: 60m),
            Balance("SCREW-05", onHand: 10_000m),
        ]);

        var result = await service.CalculateMaxBuildableAsync("TV-100");

        Assert.Equal(20m, result.MaxBuildableQty);
        Assert.Equal(CalculationBasis.Available, result.Basis);
        Assert.Equal("v3", result.BomVersion);
    }

    [Fact]
    public async Task 可製造量_沒有庫存紀錄的料件視為零而非無限()
    {
        var service = CreateService([Balance("PANEL-01", onHand: 100m)]); // SCREW-05 完全沒有庫存紀錄

        var result = await service.CalculateMaxBuildableAsync("TV-100");

        Assert.Equal(0m, result.MaxBuildableQty);
        Assert.Contains(result.ShortageComponents, c => c.ComponentCode == "SCREW-05");
    }

    [Fact]
    public async Task 指定產量_不足時列出缺料件與短少量()
    {
        var service = CreateService(
        [
            Balance("PANEL-01", onHand: 100m),   // 做 30 台需要 60，夠
            Balance("SCREW-05", onHand: 300m),   // 做 30 台需要 360，短少 60
        ]);

        var result = await service.CalculateMaxBuildableAsync("TV-100", plannedQty: 30m);

        Assert.False(result.SufficientForRequestedQty);
        var shortage = Assert.Single(result.ShortageComponents);
        Assert.Equal("SCREW-05", shortage.ComponentCode);
        Assert.Equal(12m, shortage.RequiredPerFinishedUnit);
        Assert.Equal(300m, shortage.AvailableQty);
        Assert.Equal(60m, shortage.ShortfallQty);
    }

    [Fact]
    public async Task 指定產量_充足時回報足夠且無缺料()
    {
        var service = CreateService([Balance("PANEL-01", onHand: 100m), Balance("SCREW-05", onHand: 10_000m)]);

        var result = await service.CalculateMaxBuildableAsync("TV-100", plannedQty: 30m);

        Assert.True(result.SufficientForRequestedQty);
        Assert.Empty(result.ShortageComponents);
    }

    [Fact]
    public async Task 未指定產量_連一台都做不出來時仍會列出瓶頸零件()
    {
        var service = CreateService([Balance("PANEL-01", onHand: 1m), Balance("SCREW-05", onHand: 10_000m)]);

        var result = await service.CalculateMaxBuildableAsync("TV-100");

        Assert.Equal(0m, result.MaxBuildableQty);
        Assert.Null(result.SufficientForRequestedQty);
        Assert.Equal("PANEL-01", Assert.Single(result.ShortageComponents).ComponentCode);
    }

    [Fact]
    public async Task 未指定產量_做得出來時不列缺料()
    {
        var service = CreateService([Balance("PANEL-01", onHand: 100m), Balance("SCREW-05", onHand: 10_000m)]);

        var result = await service.CalculateMaxBuildableAsync("TV-100");

        Assert.Empty(result.ShortageComponents);
        Assert.Null(result.SufficientForRequestedQty);
    }

    [Fact]
    public async Task BOM循環參照會擲出明確例外而不是無限遞迴()
    {
        var bom = Bom.Append(
            new BomLine { ParentItemCode = "SCREW-05", ComponentItemCode = "TV-100", QtyPer = 1m, BomVersion = "v1" });

        var service = new BomExplosionService(
            new InMemoryItemRepository(Items), new InMemoryBomRepository(bom), new InMemoryInventoryRepository([]));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExplodeToLeavesAsync("TV-100"));
        Assert.Contains("循環參照", ex.Message);
    }

    [Fact]
    public async Task 沒有BOM的料件無法計算可製造量()
    {
        var service = CreateService([]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CalculateMaxBuildableAsync("PANEL-01"));
        Assert.Contains("沒有 BOM", ex.Message);
    }

    [Fact]
    public async Task 查無料件時擲出可辨識的例外供AI回報查無資料()
    {
        var service = CreateService([]);

        var ex = await Assert.ThrowsAsync<EntityNotFoundException>(
            () => service.CalculateMaxBuildableAsync("NOT-EXIST"));
        Assert.Equal("料件", ex.EntityName);
    }

    [Fact]
    public async Task BOM用量為零視為資料異常()
    {
        var bom = new[]
        {
            new BomLine { ParentItemCode = "TV-100", ComponentItemCode = "PANEL-01", QtyPer = 0m, BomVersion = "v3" },
        };

        var service = new BomExplosionService(
            new InMemoryItemRepository(Items), new InMemoryBomRepository(bom), new InMemoryInventoryRepository([]));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CalculateMaxBuildableAsync("TV-100"));
    }
}
