using Erp.Application.Abstractions;

namespace Erp.Application.Quality;

public sealed record InspectionSummary(
    string WorkOrderNo,
    string ItemCode,
    decimal InspectedQty,
    decimal PassedQty,
    decimal FailedQty,
    string? FailReasonSummary);

public sealed class QualityInspectionQueryService(IQualityInspectionRepository repository)
{
    public async Task<IReadOnlyList<InspectionSummary>> GetSummaryAsync(
        string? itemCode = null,
        string? workOrderNo = null,
        DateOnly? from = null,
        DateOnly? to = null,
        CancellationToken ct = default)
    {
        var records = await repository.QueryAsync(itemCode, workOrderNo, from, to, ct);

        // 同一張工單可能有多次檢驗，彙總成一列再交給 AI，避免 LLM 自己加總數字
        return [.. records
            .GroupBy(r => (r.WorkOrderNo, r.ItemCode))
            .Select(g => new InspectionSummary(
                g.Key.WorkOrderNo,
                g.Key.ItemCode,
                g.Sum(r => r.InspectedQty),
                g.Sum(r => r.PassedQty),
                g.Sum(r => r.FailedQty),
                SummarizeReasons(g.Select(r => r.FailReason))))
            .OrderBy(s => s.WorkOrderNo, StringComparer.OrdinalIgnoreCase)];
    }

    private static string? SummarizeReasons(IEnumerable<string?> reasons)
    {
        var distinct = reasons
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return distinct.Count == 0 ? null : string.Join("、", distinct);
    }
}
