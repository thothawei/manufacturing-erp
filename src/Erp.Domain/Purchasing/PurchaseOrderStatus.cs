namespace Erp.Domain.Purchasing;

public enum PurchaseOrderStatus
{
    Open,             // 已下單未到貨
    PartiallyReceived,// 部分入庫
    Received,         // 全部入庫
    Cancelled
}
