using Erp.Domain.Purchasing;

namespace Erp.Application.Abstractions;

public interface IPurchaseOrderRepository
{
    Task<IReadOnlyList<PurchaseOrder>> GetOpenAsync(
        string? supplierCode = null, string? itemCode = null, CancellationToken ct = default);
}
