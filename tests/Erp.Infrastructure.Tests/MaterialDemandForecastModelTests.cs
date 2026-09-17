using Erp.Application.Ml;
using Erp.Infrastructure.Ml;
using Microsoft.Extensions.Logging.Abstractions;

namespace Erp.Infrastructure.Tests;

/// ONNX 物料需求預測模型的載入與推論（S6）。
///
/// 跟 DelayRiskModelTests 同一個理由：刻意不用假模型，要驗的就是 repo 裡那個
/// 模型檔真的載得起來、推論真的跑得動，而且算出來的數字與訓練當下一致。
public class MaterialDemandForecastModelTests
{
    private static readonly string SharedModelDirectory = Path.Combine(AppContext.BaseDirectory, "Ml");

    private static OnnxMaterialDemandForecastModel CreateModel(string? directory = null)
        => new(NullLogger<OnnxMaterialDemandForecastModel>.Instance, directory);

    [Fact]
    public void Repo裡的模型檔載得起來()
    {
        using var model = CreateModel();

        Assert.True(model.IsAvailable, model.Description);
        Assert.NotNull(model.Metadata);
    }

    [Fact]
    public void Metadata誠實標明資料是模擬的()
    {
        using var model = CreateModel();

        Assert.Contains("模擬資料", model.Metadata!.DataSource);
        Assert.Contains("非真實產線資料", model.Metadata.DataSource);
        Assert.Contains("模擬資料", model.Description);
    }

    /// **這條是整個檔案的重點。**訓練時 lightgbm 對這幾組輸入算出來的預測值存在 metadata 裡，
    /// 這裡驗 ONNX 載進 .NET 之後算出同一個數字。特徵順序錯位的話，推論照跑不報錯，
    /// 只是每個數字都是錯的——只有這條會紅。
    [Fact]
    public void 推論結果與訓練當下的lightgbm一致()
    {
        using var model = CreateModel();
        var samples = model.Metadata!.GoldenSamples;

        Assert.NotEmpty(samples);

        foreach (var sample in samples)
        {
            var features = ToFeatures(sample.Features);
            var actual = model.PredictDemand(features);

            // 用相對誤差而不是固定絕對誤差：延遲風險模型的輸出是 0~1 的機率，
            // 絕對誤差本來就很小；這裡的預測值是幾百到幾千的需求量，
            // ONNX Runtime 的樹狀模型累加順序跟原生 lightgbm 不完全相同，
            // 累出來的浮點誤差會隨數值量級放大，用絕對容差會在大數值品項上誤判成不一致。
            var relativeError = Math.Abs(actual - sample.ExpectedPrediction)
                / Math.Max(1.0, Math.Abs(sample.ExpectedPrediction));
            Assert.True(relativeError < 1e-3,
                $"預測值 {actual} 與訓練當下的 {sample.ExpectedPrediction} 相對誤差 {relativeError:P4}，超出容差");
        }
    }

    [Fact]
    public void Metadata裡的特徵名稱與程式碼裡的定義一致()
    {
        using var model = CreateModel();

        Assert.Equal(MaterialDemandForecastFeatures.FeatureNames, model.Metadata!.Features);
    }

    /// 反向驗證：跟 DelayRiskModelTests 的同名測試同一個理由，證明「模型檔與程式碼
    /// 版本對不上時要能當場看出來」這條防線真的接上了，不是只有離線測試釘住現況。
    [Fact]
    public void 特徵順序與metadata不一致時_模型會停用而不是照跑()
    {
        // 獨立臨時目錄，理由見 TempModelDirectory 的說明——不動同組件其他測試類別
        // 可能同時在平行讀取的共用檔案。
        using var temp = new TempModelDirectory(SharedModelDirectory, "material-demand-forecast-model");

        var mutated = File.ReadAllText(temp.MetadataPath).Replace("\"lag_1\"", "\"totally_different_feature\"");
        Assert.NotEqual(File.ReadAllText(temp.MetadataPath), mutated);
        File.WriteAllText(temp.MetadataPath, mutated);

        using var model = CreateModel(temp.Directory);

        Assert.False(model.IsAvailable, "特徵定義對不上時模型仍然回報可用");
        Assert.False(model.FeatureSchemaConsistent);
        Assert.Contains("不一致", model.Description);
        Assert.Throws<MaterialDemandForecastModelUnavailableException>(
            () => model.PredictDemand(ToFeatures([1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0])));
    }

    [Fact]
    public void 模型檔不存在時不擲例外_而是回報不可用()
    {
        using var temp = new TempModelDirectory(
            SharedModelDirectory, "material-demand-forecast-model", copyOnnx: false);

        using var model = CreateModel(temp.Directory);

        Assert.False(model.IsAvailable);
        Assert.Contains("沒有模型檔", model.Description);
        Assert.Throws<MaterialDemandForecastModelUnavailableException>(
            () => model.PredictDemand(ToFeatures([1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0])));
    }

    [Fact]
    public void 三方對照的贏家與部署模型都有記錄()
    {
        using var model = CreateModel();

        Assert.False(string.IsNullOrWhiteSpace(model.WinnerByMape));
        Assert.Equal("gbdt", model.DeployedModel);
        Assert.Contains(model.WinnerByMape!, new[] { "seasonal_naive", "gbdt", "mlp" });
    }

    /// 跟 DelayRiskModelTests 的同名測試同一個理由：逐特徵用模型真正載入的
    /// DriftProfile 構造完全複製訓練比例的觀察值，PSI 應該趨近於零。
    [Fact]
    public void 觀察值完全複製訓練比例時_每個特徵的PSI都趨近於零()
    {
        using var model = CreateModel();
        var profiles = model.Metadata!.DriftProfile!;

        foreach (var (feature, profile) in profiles)
        {
            var recent = DriftTestHelpers.ReproduceProportions(profile);

            var psi = PsiCalculator.Compute(profile.Edges, profile.Proportions, recent);

            Assert.True(psi < 1e-6, $"{feature} 的 PSI 是 {psi}，觀察值完全複製訓練比例時應該趨近於零");
        }
    }

    /// 反向驗證：跟上一條對照，證明真的偏移的資料會被抓到，不是這個防線量不到任何東西
    [Fact]
    public void 觀察值全部擠在訓練分布的同一端時_至少一個特徵的PSI超過顯著門檻()
    {
        using var model = CreateModel();

        var skewed = Enumerable.Repeat(
            new MaterialDemandForecastFeatures(
                Lag1: 0, Lag2: 0, Lag3: 0, Lag4: 0, Lag52: 0,
                RollingMean4: 0, RollingMean12: 0, WeekOfYear: 0,
                ItemIsPanel01: 0, ItemIsScrew05: 0, ItemIsCable07: 0),
            40).ToList();

        var drift = model.ComputeDrift(skewed);

        Assert.NotNull(drift);
        Assert.Contains(drift!.Values, psi => psi > PsiCalculator.SignificantThreshold);
    }

    [Fact]
    public void 沒有觀察值時回傳null_而不是零()
    {
        using var model = CreateModel();

        Assert.Null(model.ComputeDrift([]));
    }

    private static MaterialDemandForecastFeatures ToFeatures(IReadOnlyList<float> v)
        => new(v[0], v[1], v[2], v[3], v[4], v[5], v[6], (int)v[7], v[8], v[9], v[10]);
}
