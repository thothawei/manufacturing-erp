using Erp.Application.Abstractions;
using Erp.Domain.Bom;
using Erp.Domain.Inventory;
using Erp.Domain.Items;
using Erp.Domain.Production;
using Erp.Domain.Purchasing;
using Erp.Domain.Quality;

namespace Erp.Application.Tests.Fakes;

public sealed class InMemoryItemRepository(IEnumerable<Item> items, IEnumerable<ItemSupplyInfo>? supplyInfos = null)
    : IItemRepository
{
    private readonly List<Item> _items = [.. items];
    private readonly List<ItemSupplyInfo> _supplyInfos = [.. supplyInfos ?? []];

    public Task<Item?> GetByCodeAsync(string itemCode, CancellationToken ct = default)
        => Task.FromResult(_items.FirstOrDefault(i =>
            string.Equals(i.ItemCode, itemCode, StringComparison.OrdinalIgnoreCase)));

    public Task<IReadOnlyList<Item>> SearchByKeywordAsync(string keyword, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Item>>([.. _items.Where(i =>
            i.ItemCode.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
            i.ItemName.Contains(keyword, StringComparison.OrdinalIgnoreCase))]);

    public Task<IReadOnlyList<Item>> GetByCodesAsync(IReadOnlyCollection<string> itemCodes, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Item>>([.. _items.Where(i =>
            itemCodes.Contains(i.ItemCode, StringComparer.OrdinalIgnoreCase))]);

    public Task<ItemSupplyInfo?> GetSupplyInfoAsync(string itemCode, CancellationToken ct = default)
        => Task.FromResult(_supplyInfos.FirstOrDefault(s =>
            string.Equals(s.ItemCode, itemCode, StringComparison.OrdinalIgnoreCase)));

    public Task<IReadOnlyList<ItemSupplyInfo>> GetSupplyInfosAsync(
        IReadOnlyCollection<string> itemCodes, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ItemSupplyInfo>>([.. _supplyInfos.Where(s =>
            itemCodes.Contains(s.ItemCode, StringComparer.OrdinalIgnoreCase))]);
}

public sealed class InMemoryInventoryRepository(IEnumerable<InventoryBalance> balances) : IInventoryRepository
{
    private readonly List<InventoryBalance> _balances = [.. balances];

    public Task<InventoryBalance?> GetBalanceAsync(string itemCode, CancellationToken ct = default)
        => Task.FromResult(_balances.FirstOrDefault(b =>
            string.Equals(b.ItemCode, itemCode, StringComparison.OrdinalIgnoreCase)));

    public Task<IReadOnlyList<InventoryBalance>> GetBalancesAsync(
        IReadOnlyCollection<string> itemCodes, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<InventoryBalance>>([.. _balances.Where(b =>
            itemCodes.Contains(b.ItemCode, StringComparer.OrdinalIgnoreCase))]);
}

public sealed class InMemoryBomRepository(IEnumerable<BomLine> lines) : IBomRepository
{
    private readonly List<BomLine> _lines = [.. lines];

    public Task<IReadOnlyList<BomLine>> GetLinesByParentAsync(string parentItemCode, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<BomLine>>([.. _lines.Where(l =>
            string.Equals(l.ParentItemCode, parentItemCode, StringComparison.OrdinalIgnoreCase))]);
}

public sealed class InMemoryWorkOrderRepository(
    IEnumerable<WorkOrder> workOrders, IEnumerable<RoutingStep>? steps = null) : IWorkOrderRepository
{
    private readonly List<WorkOrder> _workOrders = [.. workOrders];
    private readonly List<RoutingStep> _steps = [.. steps ?? []];

    public Task<WorkOrder?> GetByNoAsync(string workOrderNo, CancellationToken ct = default)
        => Task.FromResult(_workOrders.FirstOrDefault(w =>
            string.Equals(w.WorkOrderNo, workOrderNo, StringComparison.OrdinalIgnoreCase)));

    public Task<IReadOnlyList<WorkOrder>> GetOpenWorkOrdersByDueDateAsync(
        DateOnly dueFrom, DateOnly dueTo, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<WorkOrder>>([.. _workOrders.Where(w =>
            w.IsOpen && w.DueDate >= dueFrom && w.DueDate <= dueTo)]);

    public Task<IReadOnlyList<RoutingStep>> GetRoutingStepsAsync(string workOrderNo, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RoutingStep>>([.. _steps.Where(s =>
            string.Equals(s.WorkOrderNo, workOrderNo, StringComparison.OrdinalIgnoreCase))]);
}

public sealed class InMemoryPurchaseOrderRepository(IEnumerable<PurchaseOrder> purchaseOrders) : IPurchaseOrderRepository
{
    private readonly List<PurchaseOrder> _purchaseOrders = [.. purchaseOrders];

    public Task<IReadOnlyList<PurchaseOrder>> GetOpenAsync(
        string? supplierCode = null, string? itemCode = null, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<PurchaseOrder>>([.. _purchaseOrders
            .Where(p => p.IsOpen)
            .Where(p => supplierCode is null || string.Equals(p.SupplierCode, supplierCode, StringComparison.OrdinalIgnoreCase))
            .Where(p => itemCode is null || string.Equals(p.ItemCode, itemCode, StringComparison.OrdinalIgnoreCase))]);
}

public sealed class InMemoryQualityInspectionRepository(IEnumerable<QualityInspection> inspections)
    : IQualityInspectionRepository
{
    private readonly List<QualityInspection> _inspections = [.. inspections];

    public Task<IReadOnlyList<QualityInspection>> QueryAsync(
        string? itemCode = null, string? workOrderNo = null,
        DateOnly? from = null, DateOnly? to = null, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<QualityInspection>>([.. _inspections
            .Where(i => itemCode is null || string.Equals(i.ItemCode, itemCode, StringComparison.OrdinalIgnoreCase))
            .Where(i => workOrderNo is null || string.Equals(i.WorkOrderNo, workOrderNo, StringComparison.OrdinalIgnoreCase))
            .Where(i => from is null || i.InspectedAt >= from)
            .Where(i => to is null || i.InspectedAt <= to)]);
}
