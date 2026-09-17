using System.Text.Json;
using Erp.Application.Ml;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Erp.Infrastructure.Ml;

public sealed record DemandForecastMetrics(double Mae, double Rmse, double Mape);

/// 訓練時三方對照（seasonal naive／GBDT／小型 DL）的完整結果，
/// 誠實紀錄贏家是誰，不因為部署選擇固定用 GBDT 而被蓋掉。
public sealed record DemandForecastComparison(
    string MethodNote,
    IReadOnlyDictionary<string, double> MeanMapeByMethod,
    string WinnerByMape,
    IReadOnlyDictionary<string, DemandForecastMetrics> Overall,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, DemandForecastMetrics>> ByItem);

/// 訓練時 lightgbm 對這幾組輸入算出來的預測值。理由跟 work-order-delay-model 的
/// GoldenSample 一樣：ONNX 吃的是沒有欄位名稱的張量，特徵順序錯位推論照跑不報錯，
/// 只是每個數字都是錯的。
public sealed record DemandForecastGoldenSample(
    IReadOnlyList<float> Features,
    double ExpectedPrediction);

public sealed record MaterialDemandForecastMetadata(
    string TrainedOn,
    string DataSource,
    IReadOnlyList<string> Items,
    int WeeksTotal,
    int RowsTrain,
    int RowsTest,
    int TrainEndWeek,
    IReadOnlyList<string> Features,
    DemandForecastComparison Comparison,
    string DeployedModel,
    string DeploymentNote,
    IReadOnlyList<DemandForecastGoldenSample> GoldenSamples);

/// 以 ONNX Runtime 載入離線訓練好的 GBDT 模型做物料需求預測（S6）。
///
/// 結構刻意跟 OnnxDelayRiskModel 一致（可選模組、載不起來就 IsAvailable=false、
/// 執行期比對特徵順序），這是這個 repo 對「怎麼安全地載入一個離線訓練的模型」
/// 已經定案的做法，第二個模型不需要重新發明一次。
public sealed class OnnxMaterialDemandForecastModel : IMaterialDemandForecastModel, IDisposable
{
    private const string ModelFileName = "material-demand-forecast-model.onnx";
    private const string MetadataFileName = "material-demand-forecast-model.json";

    private readonly InferenceSession? _session;
    private readonly string _inputName = "features";
    private readonly bool _schemaMismatch;

    public OnnxMaterialDemandForecastModel(ILogger<OnnxMaterialDemandForecastModel> logger)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Ml");
        var modelPath = Path.Combine(directory, ModelFileName);
        var metadataPath = Path.Combine(directory, MetadataFileName);

        if (!File.Exists(modelPath))
        {
            Description = $"沒有模型檔（找不到 {modelPath}），物料需求預測不可用";
            logger.LogWarning("找不到物料需求預測模型檔 {Path}，預測功能停用", modelPath);
            return;
        }

        try
        {
            _session = new InferenceSession(modelPath);
            _inputName = _session.InputMetadata.Keys.First();

            Metadata = File.Exists(metadataPath)
                ? JsonSerializer.Deserialize<MaterialDemandForecastMetadata>(
                    File.ReadAllText(metadataPath),
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower })
                : null;

            _schemaMismatch = Metadata is not null
                && !MaterialDemandForecastFeatures.FeatureNames.SequenceEqual(Metadata.Features);

            if (_schemaMismatch)
            {
                logger.LogError(
                    "物料需求預測模型的特徵順序與程式碼不一致，模型檔：{ModelFeatures}，程式碼：{CodeFeatures}，"
                    + "為避免用錯位的數值算出一個外觀正常但錯誤的預測，已停用預測",
                    string.Join(",", Metadata!.Features),
                    string.Join(",", MaterialDemandForecastFeatures.FeatureNames));
            }

            Description = _schemaMismatch
                ? $"模型檔的特徵定義（{string.Join("、", Metadata!.Features)}）與程式碼目前的定義"
                  + $"（{string.Join("、", MaterialDemandForecastFeatures.FeatureNames)}）不一致，"
                  + "為避免算出錯位的預測，已停用預測，請重跑 ml/train_demand_forecast.py 或還原程式碼"
                : Metadata is null
                    ? "已載入模型，但沒有 metadata"
                    : $"GBDT（LightGBM），訓練於 {Metadata.TrainedOn}，"
                      + $"資料：{Metadata.DataSource}，"
                      + $"三方對照（seasonal naive／GBDT／MLP）依平均 MAPE 的贏家是 {Metadata.Comparison.WinnerByMape}，"
                      + $"整體 MAPE {Metadata.Comparison.Overall["gbdt"].Mape:P1}";
        }
        catch (Exception ex) when (ex is OnnxRuntimeException or JsonException or IOException)
        {
            _session = null;
            Description = $"模型檔載入失敗：{ex.Message}";
            logger.LogWarning(ex, "物料需求預測模型載入失敗，預測功能停用");
        }
    }

    public bool IsAvailable => _session is not null && !_schemaMismatch;

    public string Description { get; } = "";

    public MaterialDemandForecastMetadata? Metadata { get; }

    public bool FeatureSchemaConsistent => !_schemaMismatch;

    public string? TrainedOn => Metadata?.TrainedOn;

    public string? WinnerByMape => Metadata?.Comparison.WinnerByMape;

    public string? DeployedModel => Metadata?.DeployedModel;

    public double PredictDemand(MaterialDemandForecastFeatures features)
    {
        if (_session is null || _schemaMismatch)
        {
            throw new MaterialDemandForecastModelUnavailableException(Description);
        }

        var vector = features.ToVector();
        var tensor = new DenseTensor<float>(vector, [1, vector.Length]);

        using var results = _session.Run([NamedOnnxValue.CreateFromTensor(_inputName, tensor)]);

        return results.First().AsTensor<float>()[0, 0];
    }

    public void Dispose() => _session?.Dispose();
}
