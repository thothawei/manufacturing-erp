namespace Erp.Application.Ml;

/// 模型不可用的原因。模型檔沒放進來、或載入失敗時，
/// 預測服務要能如實說「沒有模型」，而不是回一個看起來像機率的數字。
public sealed class DelayRiskModelUnavailableException(string message) : Exception(message);

/// 一個落在訓練資料分布之外的特徵。
///
/// 模型對沒見過的輸入照樣會給出一個機率，而且那個數字看起來與分布內的一樣有自信 ——
/// 這是它最危險的失敗方式：不報錯、不異常，只是可信度低而沒有人知道。
/// 把超界的欄位與訓練範圍一起回報出去，讓看到機率的人能自己判斷要不要採信。
public sealed record OutOfDistributionFeature(
    string Feature,
    double Value,
    double TrainingLow,
    double TrainingHigh);

/// 延遲風險模型的 port。
///
/// 定義在 Application、實作在 Infrastructure，理由與 ILlmClient 一樣：
/// 推論用的 ONNX Runtime 是基礎設施細節，Application 不該知道模型是用什麼跑的
/// （架構測試釘住 Application 不得參考 ONNX 套件）。
public interface IDelayRiskModel
{
    /// 模型檔存在且載得起來
    bool IsAvailable { get; }

    /// 模型的來源說明（檔名、訓練日期、AUC 等），回答時要能講出依據
    string Description { get; }

    /// 訓練時挑出來的決策閾值。
    ///
    /// 為什麼不是 0.5：漏抓一張會延遲的工單，代價是客戶端的交期跳票；
    /// 誤報的代價只是生管多看一眼。兩者不對稱，閾值就不該用對稱的預設值。
    double DecisionThreshold { get; }

    /// 模型輸出的機率有沒有經過校準（Platt／isotonic）。
    ///
    /// 沒校準的機率只保證**排序**有意義：0.68 比 0.42 更可能延遲，
    /// 但 0.68 不保證「這類工單真的有 68% 會延遲」。這兩件事常被混為一談，
    /// 所以由模型自己講出來，而不是寫死在回答文字裡。
    bool IsCalibrated { get; }

    /// metadata 宣告的特徵順序跟程式碼目前的 WorkOrderDelayFeatures.FeatureNames 對不對得上。
    ///
    /// false 時 IsAvailable 也一定是 false —— 對不上代表模型會把數值餵進錯的欄位，
    /// 那不是「品質比較差」，是「這個結果沒有意義」。給模型註冊／健康檢查這類用途看，
    /// 不需要因此把整包 metadata（含黃金樣本、特徵分布範圍等實作細節）暴露到 Application 層。
    bool FeatureSchemaConsistent { get; }

    /// 訓練日期，沒有 metadata 時為 null
    string? TrainedOn { get; }

    /// 訓練資料的來源說明（例如「模擬資料，非真實產線資料」），沒有 metadata 時為 null
    string? DataSource { get; }

    /// 訓練樣本總數，沒有 metadata 時為 null
    int? RowsTotal { get; }

    /// 訓練時的 ROC AUC，沒有 metadata 時為 null
    double? RocAuc { get; }

    /// 預測「這張工單會延遲」的機率（0~1）
    double PredictDelayProbability(WorkOrderDelayFeatures features);

    /// 找出落在訓練資料分布之外的特徵。全部在範圍內時回空清單。
    ///
    /// 訓練分布的邊界由 metadata 提供；沒有 metadata 時無從判斷，同樣回空清單
    /// —— 但那種情況下 Description 已經說了「沒有 metadata」。
    IReadOnlyList<OutOfDistributionFeature> FindOutOfDistributionFeatures(
        WorkOrderDelayFeatures features);
}
