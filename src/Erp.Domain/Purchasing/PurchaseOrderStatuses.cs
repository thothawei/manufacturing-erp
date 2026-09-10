namespace Erp.Domain.Purchasing;

/// 未結案採購單的定義，理由同 WorkOrderStatuses
public static class PurchaseOrderStatuses
{
    public static readonly PurchaseOrderStatus[] Open =
    [
        PurchaseOrderStatus.Open,
        PurchaseOrderStatus.PartiallyReceived
    ];
}
