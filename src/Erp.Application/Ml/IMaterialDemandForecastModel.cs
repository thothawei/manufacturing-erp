namespace Erp.Application.Ml;

/// 模型不可用的原因。模型檔沒放進來、或載入失敗時，
/// 預測服務要能如實說「沒有模型」，而不是回一個看起來像預測值的數字。
public sealed class MaterialDemandForecastModelUnavailableException(string message) : Exception(message);

/// 物料需求預測模型的 port（S6）。
///
/// 定義在 Application、實作在 Infrastructure，理由與 IDelayRiskModel 一樣：
/// 推論用的 ONNX Runtime 是基礎設施細節，Application 不該知道模型是用什麼跑的。
public interface IMaterialDemandForecastModel
{
    /// 模型檔存在且載得起來，而且 metadata 宣告的特徵順序跟程式碼目前的定義一致
    bool IsAvailable { get; }

    /// 模型的來源說明（訓練日期、方法、對照結果等），回答時要能講出依據
    string Description { get; }

    /// metadata 宣告的特徵順序跟 MaterialDemandForecastFeatures.FeatureNames 對不對得上。
    /// false 時 IsAvailable 也一定是 false —— 理由與 IDelayRiskModel 的同名成員一致。
    bool FeatureSchemaConsistent { get; }

    /// 訓練日期，沒有 metadata 時為 null
    string? TrainedOn { get; }

    /// 三方對照（seasonal naive／GBDT／小型 DL）依平均 MAPE 選出的贏家，沒有 metadata 時為 null。
    /// 不一定等於實際部署的方法 —— 部署的是哪個由 <see cref="DeployedModel"/> 講清楚。
    string? WinnerByMape { get; }

    /// 實際部署做推論的方法名稱（目前固定是 "gbdt"），沒有 metadata 時為 null
    string? DeployedModel { get; }

    /// 預測下一週的需求量。特徵必須由呼叫端算好提供 —— 這個系統目前沒有持久化
    /// 歷史週別需求，模型本身不會、也不能自己去查。
    double PredictDemand(MaterialDemandForecastFeatures features);

    /// 用最近一批推論請求的特徵，逐特徵算 PSI 漂移分數（S5）。
    /// 沒有 metadata（或沒有 drift bin edges）時回 null，理由與 IDelayRiskModel 的
    /// 同名成員一致：沒有訓練分布就無從比較，不是「沒有飄移」。
    IReadOnlyDictionary<string, double>? ComputeDrift(
        IReadOnlyList<MaterialDemandForecastFeatures> recentObservations);
}
