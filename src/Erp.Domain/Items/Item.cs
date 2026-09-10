namespace Erp.Domain.Items;

/// 料件主檔
public sealed class Item
{
    public required string ItemCode { get; init; }
    public required string ItemName { get; init; }
    public required ItemType ItemType { get; init; }
    public required string Unit { get; init; }
}
