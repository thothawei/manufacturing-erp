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
    DelayRiskCalibration? Calibration,
    IReadOnlyDictionary<string, FeatureRange>? FeatureRanges,
    IReadOnlyList<GoldenSample> GoldenSamples);

/// 訓練時對「要不要做機率校準」的評估結果。
///
/// Applied 是 null 代表評估過但不採用 —— 那不是漏做，是有數字支持的決定，
/// 理由在 Decision 裡。訓練腳本用 bootstrap 算 ΔBrier 的 95% 區間，
/// 跨 0 就代表這批測試資料分不出校準前後的差別。
public sealed record DelayRiskCalibration(
    UncalibratedScores Uncalibrated,
    string? Applied,
    string Decision);

public sealed record UncalibratedScores(double Brier, double Ece);

/// 某個特徵在訓練資料裡的分布範圍。
///
/// 線上用 P1/P99 判斷分布外，而不是 Min/Max：後者被單一一筆極端值決定，
/// 只要線上有一張工單稍微超過那一筆，就會整批被標成分布外。
public sealed record FeatureRange(double P1, double P99, double Min, double Max);

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

    /// metadata 宣告的特徵順序跟 WorkOrderDelayFeatures.FeatureNames 對不上。
    ///
    /// 這是 S5（docs/ml-dl-llm-strengthening-plan-v1.md）明講的那個事故：
    /// 「模型檔與程式碼版本對不上時要能當場看出來」。ONNX 吃的是沒有欄位名稱的張量，
    /// 對不上時不會報錯，只會把數值餵進錯的欄位，算出一個外觀正常但語意錯誤的機率——
    /// 跟分布外輸入是同一種「不報錯的失敗」，所以處理方式也一樣：停用，而不是照跑。
    /// `Metadata裡的特徵名稱與程式碼裡的定義一致` 那條測試在 CI 就會抓到這件事，
    /// 這裡是多一層執行期防線，防的是「metadata 手動改過、但沒人重跑過測試」這種情況。
    private readonly bool _schemaMismatch;

    /// modelDirectory 只給測試用：指向一個臨時目錄，讓「模型檔不存在」「metadata 跟
    /// 程式碼對不上」這類測試不用動到 AppContext.BaseDirectory 底下那份共用檔案 ——
    /// 那份檔案在 xUnit 預設的平行測試下，同一個組件裡其他測試類別（凡是會建立
    /// 真的 OnnxDelayRiskModel 的）隨時可能讀到，動它會是一個間歇性、跟被測程式碼
    /// 無關的假紅燈。
    public OnnxDelayRiskModel(ILogger<OnnxDelayRiskModel> logger, string? modelDirectory = null)
    {
        var directory = modelDirectory ?? Path.Combine(AppContext.BaseDirectory, "Ml");
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

            _schemaMismatch = Metadata is not null
                && !WorkOrderDelayFeatures.FeatureNames.SequenceEqual(Metadata.Features);

            if (_schemaMismatch)
            {
                logger.LogError(
                    "延遲風險模型的特徵順序與程式碼不一致，模型檔：{ModelFeatures}，程式碼：{CodeFeatures}，"
                    + "為避免用錯位的數值算出一個外觀正常但錯誤的機率，已停用預測",
                    string.Join(",", Metadata!.Features), string.Join(",", WorkOrderDelayFeatures.FeatureNames));
            }

            Description = _schemaMismatch
                ? $"模型檔的特徵定義（{string.Join("、", Metadata!.Features)}）與程式碼目前的定義"
                  + $"（{string.Join("、", WorkOrderDelayFeatures.FeatureNames)}）不一致，"
                  + "為避免算出錯位的機率，已停用預測，請重跑 ml/train.py 或還原程式碼"
                : Metadata is null
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

    public bool IsAvailable => _session is not null && !_schemaMismatch;

    public string Description { get; } = "";

    public DelayRiskModelMetadata? Metadata { get; }

    /// 訓練時挑出來的決策閾值。沒有 metadata 時退回 0.5 —— 那是最保守的預設，
    /// 但也代表「不對稱代價」那件事沒有被反映，所以 Description 會講明沒有 metadata。
    public double DecisionThreshold => Metadata?.Metrics.Threshold ?? 0.5;

    /// 目前是 false，而且那是有數字支持的決定而不是漏做：
    /// 訓練時跑過 Platt 與 isotonic，兩者 ΔBrier 的 95% bootstrap 區間都跨 0，
    /// 也就是這批測試資料分不出校準前後的差別。採用一個分不出差別的轉換，
    /// 只是多一層沒有證據支持的加工。詳見 metadata 的 calibration.decision。
    public bool IsCalibrated => Metadata?.Calibration?.Applied is not null;

    /// 沒有 metadata 時無從比對，回傳 true——那種情況下 IsAvailable 已經是 false 了
    /// （沒有模型檔或模型檔載入失敗），不需要再疊一個「不一致」的訊號讓人誤會成別的問題。
    public bool FeatureSchemaConsistent => !_schemaMismatch;

    public string? TrainedOn => Metadata?.TrainedOn;

    public string? DataSource => Metadata?.DataSource;

    public int? RowsTotal => Metadata?.RowsTotal;

    public double? RocAuc => Metadata?.Metrics.RocAuc;

    public double PredictDelayProbability(WorkOrderDelayFeatures features)
    {
        if (_session is null || _schemaMismatch)
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

    /// 分布外檢查。
    ///
    /// 這是模型最危險的失敗方式：對沒見過的輸入它照樣給一個機率，
    /// 數字的外觀與分布內的完全一樣，只是可信度低 —— 而且不會有任何徵兆。
    /// 展示資料的 weekly_load_ratio 是 0.125，訓練資料的下界是 0.5088，
    /// 這個檢查就是為了讓那件事在回應裡看得見。
    public IReadOnlyList<OutOfDistributionFeature> FindOutOfDistributionFeatures(
        WorkOrderDelayFeatures features)
    {
        if (Metadata?.FeatureRanges is not { Count: > 0 } ranges)
        {
            return [];   // 沒有邊界資料就無從判斷，不要猜
        }

        var values = features.ToVector();
        var names = WorkOrderDelayFeatures.FeatureNames;
        var outOfRange = new List<OutOfDistributionFeature>();

        for (var i = 0; i < names.Count && i < values.Length; i++)
        {
            if (!ranges.TryGetValue(names[i], out var range))
            {
                continue;
            }

            // 容差不是小心過頭，是必要的：特徵向量是 float32（ONNX 吃的型別），
            // 邊界是 metadata 裡的 double。訓練集裡值剛好等於邊界的樣本，
            // 轉成 float 之後會變成 0.11999999731779099 這種數字，
            // 直接比大小就會把它判成分布外 —— 這是測試抓到的，不是預想出來的。
            var tolerance = Math.Max(1e-6, (range.P99 - range.P1) * 1e-6);

            if (values[i] < range.P1 - tolerance || values[i] > range.P99 + tolerance)
            {
                outOfRange.Add(new OutOfDistributionFeature(
                    names[i], values[i], range.P1, range.P99));
            }
        }

        return outOfRange;
    }

    public void Dispose() => _session?.Dispose();
}
