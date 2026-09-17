using Erp.Application.Ml;

namespace Erp.Application.Tests.Fakes;

/// Application 層測的是「特徵有沒有從真實資料正確算出來」，
/// 不是「ONNX 推論對不對」—— 後者需要真的模型檔，在 Infrastructure 那層驗
/// （DelayRiskModelTests 用的是 repo 裡真正的模型，不是假的）。
///
/// 這個假模型把收到的特徵留下來，讓測試可以逐欄檢查。
public sealed class FakeDelayRiskModel(
    bool available = true,
    double probability = 0.42,
    IReadOnlyList<OutOfDistributionFeature>? outOfDistribution = null) : IDelayRiskModel
{
    public WorkOrderDelayFeatures? LastFeatures { get; private set; }

    public bool IsAvailable { get; } = available;

    public string Description => IsAvailable ? "假模型（測試用）" : "沒有模型檔（測試用）";

    public double DecisionThreshold => 0.26;

    public bool IsCalibrated => false;

    public bool FeatureSchemaConsistent => true;

    public string? TrainedOn => null;

    public string? DataSource => null;

    public int? RowsTotal => null;

    public double? RocAuc => null;

    public double PredictDelayProbability(WorkOrderDelayFeatures features)
    {
        if (!IsAvailable)
        {
            throw new DelayRiskModelUnavailableException(Description);
        }

        LastFeatures = features;
        return probability;
    }

    public IReadOnlyList<OutOfDistributionFeature> FindOutOfDistributionFeatures(
        WorkOrderDelayFeatures features) => outOfDistribution ?? [];

    public IReadOnlyDictionary<string, double>? ComputeDrift(
        IReadOnlyList<WorkOrderDelayFeatures> recentObservations) => null;
}
