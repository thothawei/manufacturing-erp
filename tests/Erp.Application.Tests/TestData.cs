using Erp.Domain.Bom;
using Erp.Domain.Inventory;
using Erp.Domain.Items;

namespace Erp.Application.Tests;

/// 跨測試共用的 TV-100 產品結構，與 docs/ai-assistant-module-plan-v2.md 第 3.1 節一致
internal static class TestData
{
    public static readonly Item[] Items =
    [
        new() { ItemCode = "TV-100", ItemName = "電視機", ItemType = ItemType.FinishedGood, Unit = "台" },
        new() { ItemCode = "PANEL-01", ItemName = "面板", ItemType = ItemType.RawMaterial, Unit = "片" },
        new() { ItemCode = "CHASSIS-02", ItemName = "機殼組件", ItemType = ItemType.SemiFinished, Unit = "組" },
        new() { ItemCode = "SCREW-05", ItemName = "螺絲", ItemType = ItemType.RawMaterial, Unit = "支" },
    ];

    public static readonly BomLine[] Bom =
    [
        new() { ParentItemCode = "TV-100", ComponentItemCode = "PANEL-01", QtyPer = 2m, BomVersion = "v3" },
        new() { ParentItemCode = "TV-100", ComponentItemCode = "CHASSIS-02", QtyPer = 3m, BomVersion = "v3" },
        new() { ParentItemCode = "CHASSIS-02", ComponentItemCode = "SCREW-05", QtyPer = 4m, BomVersion = "v1" },
    ];

    public static readonly ItemSupplyInfo[] SupplyInfos =
    [
        new() { ItemCode = "PANEL-01", SupplierCode = "SUP-008", LeadTimeDays = 5, OrderMultiple = 50m },
        new() { ItemCode = "SCREW-05", SupplierCode = "SUP-021", LeadTimeDays = 2, MinOrderQty = 1_000m },
    ];

    public static InventoryBalance Balance(string code, decimal onHand, decimal reserved = 0m) =>
        new() { ItemCode = code, OnHandQty = onHand, ReservedQty = reserved };
}
