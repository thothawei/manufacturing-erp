using Erp.Domain.Bom;

namespace Erp.Application.Abstractions;

public interface IBomRepository
{
    /// 取得父件的 BOM 展開行；父件沒有 BOM（＝葉節點）時回傳空集合
    Task<IReadOnlyList<BomLine>> GetLinesByParentAsync(string parentItemCode, CancellationToken ct = default);
}
