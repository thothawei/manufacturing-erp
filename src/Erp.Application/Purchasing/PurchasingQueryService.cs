using Erp.Application.Abstractions;
using Erp.Domain.Purchasing;

namespace Erp.Application.Purchasing;

public sealed record OpenPurchaseOrder(
    string PoNo,
    string SupplierCode,
    string ItemCode,
    decimal OrderedQty,
    decimal ReceivedQty,
    DateOnly ExpectedArrivalDate,
    PurchaseOrderStatus Status);

public sealed class PurchasingQueryService(IPurchaseOrderRepository purchaseOrderRepository)
{
    public async Task<IReadOnlyList<OpenPurchaseOrder>> GetOpenPurchaseOrdersAsync(
        string? supplierCode = null, string? itemCode = null, CancellationToken ct = default)
    {
        var pos = await purchaseOrderRepository.GetOpenAsync(supplierCode, itemCode, ct);

        return [.. pos
            .OrderBy(p => p.ExpectedArrivalDate)
            .ThenBy(p => p.PoNo, StringComparer.OrdinalIgnoreCase)
            .Select(p => new OpenPurchaseOrder(
                p.PoNo, p.SupplierCode, p.ItemCode,
                p.OrderedQty, p.ReceivedQty, p.ExpectedArrivalDate, p.Status))];
    }
}
