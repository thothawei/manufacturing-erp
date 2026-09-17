using Erp.Application.Ml;
using Erp.Infrastructure.Ml;
using Microsoft.Extensions.Logging.Abstractions;

namespace Erp.Infrastructure.Tests;

/// ONNX 模型的載入與推論。
///
/// 這組刻意**不用假模型**：要驗的就是 repo 裡那個模型檔真的載得起來、
/// 推論真的跑得動，而且算出來的數字與訓練當下一致。用假推論器的話，
/// 整段 ONNX 路徑會完全沒被測到，而那正是最容易無聲出錯的地方。
public class DelayRiskModelTests
{
    private static readonly string SharedModelDirectory = Path.Combine(AppContext.BaseDirectory, "Ml");

    private static OnnxDelayRiskModel CreateModel(string? directory = null)
        => new(NullLogger<OnnxDelayRiskModel>.Instance, directory);

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
        // 這不是形式檢查：整個模組的可信度建立在「有沒有把這件事講清楚」上。
        // 有人重訓時把 data_source 改掉、卻忘了改文件敘述，這條會紅。
        using var model = CreateModel();

        Assert.Contains("模擬資料", model.Metadata!.DataSource);
        Assert.Contains("非真實產線資料", model.Metadata.DataSource);
        Assert.Contains("模擬資料", model.Description);
    }

    /// **這條是整個檔案的重點。**
    ///
    /// 訓練時 sklearn 對這幾組輸入算出來的機率存在 metadata 裡，
    /// 這裡驗 ONNX 載進 .NET 之後算出同一個數字。
    /// 特徵順序錯位、或取錯機率欄位（第 0 欄是「不延遲」），
    /// 推論都會照跑不報錯 —— 只有這條會紅。
    [Fact]
    public void 推論結果與訓練當下的sklearn一致()
    {
        using var model = CreateModel();
        var samples = model.Metadata!.GoldenSamples;

        Assert.NotEmpty(samples);

        foreach (var sample in samples)
        {
            var features = ToFeatures(sample.Features);
            var actual = model.PredictDelayProbability(features);

            // 用絕對誤差而不是「比到小數第幾位」：後者走的是銀行家捨入，
            // 曾經有一個樣本的期望值剛好落在中點上，兩邊各捨到不同方向而紅掉，
            // 而那跟推論正確與否完全無關
            Assert.Equal(sample.ExpectedProbability, actual, tolerance: 1e-6);
        }
    }

    [Fact]
    public void 機率永遠落在零到一之間()
    {
        using var model = CreateModel();

        // 刻意送極端輸入：齊套率 0、已逾期很久、前置期很長、完全沒進度
        var worst = new WorkOrderDelayFeatures(0, -30, 60, 0, 1, 5, 20);
        var best = new WorkOrderDelayFeatures(1, 120, 0, 1, 0, 0.1, 1);

        Assert.InRange(model.PredictDelayProbability(worst), 0, 1);
        Assert.InRange(model.PredictDelayProbability(best), 0, 1);
    }

    [Fact]
    public void 特徵往壞的方向走機率要變高()
    {
        // 這條不驗準確率，驗的是「模型的方向沒有整個反過來」——
        // 取錯機率欄位時，上一條黃金樣本會紅，但這條的失敗訊息更說得出哪裡不對
        using var model = CreateModel();

        var comfortable = new WorkOrderDelayFeatures(
            MaterialReadiness: 1.0, DaysUntilDue: 20, MaxLeadTimeDays: 0,
            ProgressRatio: 0.9, ItemOverdueRate: 0.1, WeeklyLoadRatio: 0.6, BomComponentCount: 3);

        var tight = comfortable with { MaterialReadiness = 0.2, DaysUntilDue = 1, MaxLeadTimeDays = 10 };

        Assert.True(
            model.PredictDelayProbability(tight) > model.PredictDelayProbability(comfortable),
            "缺料又快到期的工單，預測機率竟然沒有比較高");
    }

    [Fact]
    public void 決策閾值來自訓練時的選擇而不是預設的零點五()
    {
        // 漏抓（FN）比誤報（FP）代價高，所以閾值是壓低過的
        using var model = CreateModel();

        Assert.Equal(model.Metadata!.Metrics.Threshold, model.DecisionThreshold);
        Assert.True(model.DecisionThreshold < 0.5,
            $"決策閾值是 {model.DecisionThreshold}，沒有反映「漏抓比誤報代價高」這個判斷");
    }

    [Fact]
    public void Metadata裡的特徵名稱與程式碼裡的定義一致()
    {
        // 兩邊漂掉時 ONNX 不會報錯，只會算出錯位的結果
        using var model = CreateModel();

        Assert.Equal(WorkOrderDelayFeatures.FeatureNames, model.Metadata!.Features);
    }

    /// 上一條測試釘的是「目前這份 metadata 是對的」，這條釘的是
    /// 「metadata 對不上時，模型會不會真的停用」—— 兩者是不同的斷言，
    /// 前者綠燈不代表後者的防線接上了。S5（docs/ml-dl-llm-strengthening-plan-v1.md）
    /// 明講的事故就是這個：模型檔與程式碼版本對不上時要能當場看出來，
    /// 而不是照跑一個外觀正常但用錯欄位算出來的機率。
    [Fact]
    public void 特徵順序與metadata不一致時_模型會停用而不是照跑()
    {
        // 在獨立的臨時目錄裡操作，不動 AppContext.BaseDirectory 底下那份共用檔案 ——
        // 那份檔案在 xUnit 預設的平行測試下，同組件其他測試類別（凡是會建立真的
        // OnnxDelayRiskModel 的，例如 AiAssistantScenarioTests）隨時可能同時在讀，
        // 動它會是一個間歇性、跟被測程式碼無關的假紅燈。
        using var temp = new TempModelDirectory(SharedModelDirectory, "work-order-delay-model");

        var mutated = File.ReadAllText(temp.MetadataPath)
            .Replace("\"material_readiness\"", "\"totally_different_feature\"");
        Assert.NotEqual(File.ReadAllText(temp.MetadataPath), mutated); // 確認真的換到了，不是誤判字串沒命中
        File.WriteAllText(temp.MetadataPath, mutated);

        using var model = CreateModel(temp.Directory);

        Assert.False(model.IsAvailable, "特徵定義對不上時模型仍然回報可用");
        Assert.Contains("不一致", model.Description);
        Assert.Throws<DelayRiskModelUnavailableException>(
            () => model.PredictDelayProbability(new WorkOrderDelayFeatures(1, 1, 1, 1, 1, 1, 1)));
    }

    [Fact]
    public void 模型檔不存在時不擲例外_而是回報不可用()
    {
        // 可選模組：模型沒放進來時整個服務仍要正常啟動，
        // 而且不能回一個看起來像機率的預設值 —— 那是最糟的失敗方式。
        // 空的臨時目錄就代表「模型檔沒放進來」，不需要動共用檔案（理由同上）。
        using var temp = new TempModelDirectory(SharedModelDirectory, "work-order-delay-model", copyOnnx: false);

        using var model = CreateModel(temp.Directory);

        Assert.False(model.IsAvailable);
        Assert.Contains("沒有模型檔", model.Description);
        Assert.Throws<DelayRiskModelUnavailableException>(
            () => model.PredictDelayProbability(new WorkOrderDelayFeatures(1, 1, 1, 1, 1, 1, 1)));
    }

    [Fact]
    public void Metadata裡有校準評估的結果_不採用也要是有理由的決定()
    {
        // 「沒做校準」與「評估過、數字不支持做」是兩件事。
        // 這條釘的是後者：calibration 區塊不見了（例如有人重寫訓練腳本時砍掉）就會紅。
        using var model = CreateModel();
        var calibration = model.Metadata!.Calibration;

        Assert.NotNull(calibration);
        Assert.True(calibration.Uncalibrated.Brier > 0, "沒有未校準的 Brier score，等於沒評估過");
        Assert.False(string.IsNullOrWhiteSpace(calibration.Decision));

        // 採用與否都可以，但 IsCalibrated 必須與 metadata 說的一致 ——
        // 兩邊講不同的話，回答裡「這個機率能不能當發生率解讀」就會是錯的
        Assert.Equal(calibration.Applied is not null, model.IsCalibrated);
    }

    [Fact]
    public void Metadata裡有七個特徵的訓練分布範圍()
    {
        using var model = CreateModel();
        var ranges = model.Metadata!.FeatureRanges;

        Assert.NotNull(ranges);

        foreach (var name in WorkOrderDelayFeatures.FeatureNames)
        {
            Assert.True(ranges.TryGetValue(name, out var range), $"{name} 沒有分布範圍");
            Assert.True(range!.P1 <= range.P99, $"{name} 的 p1 比 p99 大");
            Assert.True(range.Min <= range.P1 && range.P99 <= range.Max,
                $"{name} 的百分位落在 min/max 之外，八成是欄位對映錯了");
        }
    }

    /// **這條是 OOD 檢查的重點。**
    ///
    /// 展示資料的 weekly_load_ratio 算出來是 0.125，而訓練資料的下界是 0.5 ——
    /// 模型對這種輸入照樣會給一個機率，外觀與分布內的完全一樣。
    /// 沒有這個檢查，那個機率會安靜地被當成可信的數字用。
    [Fact]
    public void 分布外的輸入要被標出來_而且說得出訓練範圍()
    {
        using var model = CreateModel();

        var features = new WorkOrderDelayFeatures(
            MaterialReadiness: 1.0, DaysUntilDue: 5, MaxLeadTimeDays: 0,
            ProgressRatio: 0.5, ItemOverdueRate: 0.25,
            WeeklyLoadRatio: 0.125,        // 訓練分布是 0.5 以上
            BomComponentCount: 4);

        var outOfRange = model.FindOutOfDistributionFeatures(features);

        var load = Assert.Single(outOfRange, f => f.Feature == "weekly_load_ratio");
        Assert.Equal(0.125, load.Value, tolerance: 1e-6);
        Assert.True(load.Value < load.TrainingLow,
            $"值 {load.Value} 沒有低於回報的訓練下界 {load.TrainingLow}，這個標記等於沒意義");
    }

    [Fact]
    public void 分布內的輸入不會被標成分布外()
    {
        // 黃金樣本本來就取自測試集，每一筆都該落在訓練分布裡。
        // 這條防的是「把邊界寫太窄、什麼都標成 OOD」——
        // 那種警告發久了就沒有人看，比不做還糟
        using var model = CreateModel();

        foreach (var sample in model.Metadata!.GoldenSamples)
        {
            var outOfRange = model.FindOutOfDistributionFeatures(ToFeatures(sample.Features));

            Assert.True(outOfRange.Count == 0,
                "訓練集裡的樣本被標成分布外："
                + string.Join("、", outOfRange.Select(f => $"{f.Feature}={f.Value}")));
        }
    }

    /// **這是漂移偵測的重點。**
    ///
    /// 每個特徵各自的分箱數不同（低基數特徵去重後箱數比 10 少），沒辦法用同一組
    /// WorkOrderDelayFeatures 記錄同時讓七個特徵都對齊各自的箱數，所以這條直接用
    /// 模型真正載進來的 DriftProfile，逐特徵構造「完全複製訓練比例」的觀察值，
    /// 驗 PsiCalculator 接的是這個模型真實的（可能是去重、非均勻的）漂移基準，
    /// 不是憑空造一組理想化的邊界。
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

    /// 反向驗證：上一條測試證明「像訓練分布的資料 PSI 低」，這條證明「真的偏移的資料
    /// PSI 會高」——兩者對照才證明這個防線量得到東西，不是隨便算一個都會過的數字。
    [Fact]
    public void 觀察值全部擠在訓練分布的同一端時_至少一個特徵的PSI超過顯著門檻()
    {
        using var model = CreateModel();
        var ranges = model.Metadata!.FeatureRanges!;

        // 全部釘在每個特徵訓練範圍的最小值——真實情境類似「上游系統壞掉、
        // 每個請求的某個欄位都變成 0 或最小值」
        var skewed = Enumerable.Repeat(
            new WorkOrderDelayFeatures(
                MaterialReadiness: ranges["material_readiness"].Min,
                DaysUntilDue: ranges["days_until_due"].Min,
                MaxLeadTimeDays: ranges["max_lead_time_days"].Min,
                ProgressRatio: ranges["progress_ratio"].Min,
                ItemOverdueRate: ranges["item_overdue_rate"].Min,
                WeeklyLoadRatio: ranges["weekly_load_ratio"].Min,
                BomComponentCount: ranges["bom_component_count"].Min),
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

    private static WorkOrderDelayFeatures ToFeatures(IReadOnlyList<float> v)
        => new(v[0], v[1], v[2], v[3], v[4], v[5], v[6]);
}
