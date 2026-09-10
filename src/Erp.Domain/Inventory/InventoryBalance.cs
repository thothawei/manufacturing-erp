namespace Erp.Domain.Inventory;

/// 庫存餘額。
/// AvailableQty 是全系統唯一的「可用量」定義，所有可行性計算都以它為基準
/// （見 docs/ai-assistant-module-plan-v2.md 第 2 節）。
public sealed class InventoryBalance
{
    public required string ItemCode { get; init; }
    public required decimal OnHandQty { get; init; }

    /// 已被未完工工單保留、不可再配置給新需求的數量
    public required decimal ReservedQty { get; init; }

    public decimal AvailableQty => OnHandQty - ReservedQty;
}
