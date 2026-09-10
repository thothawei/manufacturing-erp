namespace Erp.Domain.Bom;

/// BOM 單行：父件用掉多少個子件
public sealed class BomLine
{
    public required string ParentItemCode { get; init; }
    public required string ComponentItemCode { get; init; }

    /// 父件「一個單位」需要的子件數量
    public required decimal QtyPer { get; init; }

    public required string BomVersion { get; init; }
}
