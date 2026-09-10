namespace Erp.Domain.Items;

/// 料件類型：決定它在 BOM 樹中的角色
public enum ItemType
{
    FinishedGood,   // 成品
    SemiFinished,   // 半成品
    RawMaterial     // 原物料
}
