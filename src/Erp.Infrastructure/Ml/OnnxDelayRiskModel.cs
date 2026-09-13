using System.Text.Json;
using Erp.Application.Ml;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Erp.Infrastructure.Ml;

/// 訓練當下的評估數據。與模型檔一起進 repo，
/// 讓「這個機率是什麼東西算出來的」講得出依據，而不是一個沒有來歷的數字。
public sealed record DelayRiskModelMetadata(
    string TrainedOn,
    string DataSource,
    int RowsTotal,
    IReadOnlyList<string> Features,
    DelayRiskModelMetrics Metrics,
    IReadOnlyList<GoldenSample> GoldenSamples);

/// 訓練時 sklearn 對這組輸入算出來的機率。
///
/// 它防的是一個不會報錯的失敗：ONNX 吃的是沒有欄位名稱的張量，
/// 特徵順序錯位、或機率取到第 0 欄（不延遲）而不是第 1 欄，推論都會照跑，
/// 只是每個數字都是錯的。沒有這組樣本就沒有人會發現。
public sealed record GoldenSample(
    IReadOnlyList<float> Features,
    double ExpectedProbability);

public sealed record DelayRiskModelMetrics(
    double RocAuc,
    double Threshold,
    double PrecisionAtThreshold,
    double RecallAtThreshold);

/// 以 ONNX Runtime 載入離線訓練好的模型做推論。
///
/// 為什麼是離線訓練 + 靜態模型檔，而不是線上重訓：作品集規模下，
/// 線上重訓需要的資料回流、標籤延遲（一張工單要等到完工才知道有沒有延遲）、
/// 模型版本管理，每一項都是獨立的工程，做半套只會是假的。
/// 靜態模型檔是誠實的取捨 —— 換模型就是換一個檔案，重跑 ml/train.py。
///
/// 模型檔載不起來時**不擲例外**：這是可選模組，跟 RAG 一樣。
/// 沒有模型時 IsAvailable 是 false，預測服務會如實說「沒有模型」，
/// 而不是回一個看起來像機率的預設值 —— 那是最糟的失敗方式。
public sealed class OnnxDelayRiskModel : IDelayRiskModel, IDisposable
{
    private const string ModelFileName = "work-order-delay-model.onnx";
    private const string MetadataFileName = "work-order-delay-model.json";

    private readonly InferenceSession? _session;
    private readonly string _inputName = "features";

    public OnnxDelayRiskModel(ILogger<OnnxDelayRiskModel> logger)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Ml");
        var modelPath = Path.Combine(directory, ModelFileName);
        var metadataPath = Path.Combine(directory, MetadataFileName);

        if (!File.Exists(modelPath))
        {
            Description = $"沒有模型檔（找不到 {modelPath}），延遲風險預測不可用";
            logger.LogWarning("找不到延遲風險模型檔 {Path}，預測功能停用", modelPath);
            return;
        }

        try
        {
            _session = new InferenceSession(modelPath);
            _inputName = _session.InputMetadata.Keys.First();

            Metadata = File.Exists(metadataPath)
                ? JsonSerializer.Deserialize<DelayRiskModelMetadata>(
                    File.ReadAllText(metadataPath),
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower })
                : null;

            Description = Metadata is null
                ? "已載入模型，但沒有 metadata"
                : $"logistic regression，訓練於 {Metadata.TrainedOn}，"
                  + $"資料：{Metadata.DataSource}，樣本 {Metadata.RowsTotal} 筆，"
                  + $"ROC AUC {Metadata.Metrics.RocAuc:F4}，"
                  + $"決策閾值 {Metadata.Metrics.Threshold:F2}"
                  + $"（precision {Metadata.Metrics.PrecisionAtThreshold:F4}、"
                  + $"recall {Metadata.Metrics.RecallAtThreshold:F4}）";
        }
        catch (Exception ex) when (ex is OnnxRuntimeException or JsonException or IOException)
        {
            // 模型檔壞掉不該讓整個服務起不來 —— 它是可選模組
            _session = null;
            Description = $"模型檔載入失敗：{ex.Message}";
            logger.LogWarning(ex, "延遲風險模型載入失敗，預測功能停用");
        }
    }

    public bool IsAvailable => _session is not null;

    public string Description { get; } = "";

    public DelayRiskModelMetadata? Metadata { get; }

    /// 訓練時挑出來的決策閾值。沒有 metadata 時退回 0.5 —— 那是最保守的預設，
    /// 但也代表「不對稱代價」那件事沒有被反映，所以 Description 會講明沒有 metadata。
    public double DecisionThreshold => Metadata?.Metrics.Threshold ?? 0.5;

    public double PredictDelayProbability(WorkOrderDelayFeatures features)
    {
        if (_session is null)
        {
            throw new DelayRiskModelUnavailableException(Description);
        }

        var vector = features.ToVector();
        var tensor = new DenseTensor<float>(vector, [1, vector.Length]);

        using var results = _session.Run(
            [NamedOnnxValue.CreateFromTensor(_inputName, tensor)]);

        // 訓練時關掉了 zipmap，所以機率輸出是一個 [N, 2] 的張量，
        // 第二欄才是「會延遲」的機率。取第一欄會得到一個看起來合理但完全相反的數字。
        var probabilities = results
            .First(r => r.AsTensor<float>() is { Dimensions.Length: 2 } t && t.Dimensions[1] == 2)
            .AsTensor<float>();

        return probabilities[0, 1];
    }

    public void Dispose() => _session?.Dispose();
}
