using Erp.Domain.Purchasing;

namespace Erp.Application.Abstractions;

public interface IPurchaseOrderRepository
{
    Task<IReadOnlyList<PurchaseOrder>> GetOpenAsync(
        string? supplierCode = null, string? itemCode = null, CancellationToken ct = default);

    /// 核准採購建議時建立正式採購單。這是整個系統唯一會新增採購單的路徑，
    /// 而它只有人工確認端點呼叫得到 —— AI 的工具目錄裡沒有任何東西通到這裡。
    Task AddAsync(PurchaseOrder purchaseOrder, CancellationToken ct = default);

    /// 單號前綴相符的採購單有幾張，用來編當天的流水號
    Task<int> CountByPoNoPrefixAsync(string prefix, CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);
}
