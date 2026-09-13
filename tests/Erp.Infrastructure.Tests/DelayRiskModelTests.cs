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
    private static OnnxDelayRiskModel CreateModel()
        => new(NullLogger<OnnxDelayRiskModel>.Instance);

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

    [Fact]
    public void 模型檔不存在時不擲例外_而是回報不可用()
    {
        // 可選模組：模型沒放進來時整個服務仍要正常啟動，
        // 而且不能回一個看起來像機率的預設值 —— 那是最糟的失敗方式
        var original = Path.Combine(AppContext.BaseDirectory, "Ml", "work-order-delay-model.onnx");
        var backup = original + ".bak";

        File.Move(original, backup);
        try
        {
            using var model = CreateModel();

            Assert.False(model.IsAvailable);
            Assert.Contains("沒有模型檔", model.Description);
            Assert.Throws<DelayRiskModelUnavailableException>(
                () => model.PredictDelayProbability(new WorkOrderDelayFeatures(1, 1, 1, 1, 1, 1, 1)));
        }
        finally
        {
            File.Move(backup, original);
        }
    }

    private static WorkOrderDelayFeatures ToFeatures(IReadOnlyList<float> v)
        => new(v[0], v[1], v[2], v[3], v[4], v[5], v[6]);
}
