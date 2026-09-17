using Erp.Application.Ml;

namespace Erp.Application.Tests.Fakes;

/// Application 層測的是「服務有沒有記錄特徵」，不是「記錄機制本身對不對」——
/// 後者是純粹的資料結構邏輯，用真的 InMemoryRecentPredictionLog 測（在 Infrastructure 那層，
/// 這裡是 Application.Tests，架構規則不許往下相依 Infrastructure）。
public sealed class FakeRecentPredictionLog<TFeatures> : IRecentPredictionLog<TFeatures>
{
    private readonly List<TFeatures> _recorded = [];

    public IReadOnlyList<TFeatures> Recorded => _recorded;

    public void Record(TFeatures features) => _recorded.Add(features);

    public IReadOnlyList<TFeatures> GetRecent(int maxCount) => [.. _recorded.TakeLast(maxCount)];
}
