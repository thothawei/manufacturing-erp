using Erp.Domain.Quality;

namespace Erp.Application.Abstractions;

public interface IQualityInspectionRepository
{
    Task<IReadOnlyList<QualityInspection>> QueryAsync(
        string? itemCode = null,
        string? workOrderNo = null,
        DateOnly? from = null,
        DateOnly? to = null,
        CancellationToken ct = default);
}
