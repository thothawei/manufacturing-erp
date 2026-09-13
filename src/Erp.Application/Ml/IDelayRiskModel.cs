namespace Erp.Application.Ml;

/// 模型不可用的原因。模型檔沒放進來、或載入失敗時，
/// 預測服務要能如實說「沒有模型」，而不是回一個看起來像機率的數字。
public sealed class DelayRiskModelUnavailableException(string message) : Exception(message);

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

    /// 預測「這張工單會延遲」的機率（0~1）
    double PredictDelayProbability(WorkOrderDelayFeatures features);
}
