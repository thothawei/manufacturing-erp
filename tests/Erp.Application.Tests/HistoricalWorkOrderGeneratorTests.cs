using Erp.Application.Ml;

namespace Erp.Application.Tests;

/// 模擬資料生成器。
///
/// 驗的不是「資料像不像真的」（那沒有客觀標準），而是三件會讓訓練整個失效的事：
/// 可重現、雜訊真的加進去了、CSV 欄位順序與特徵定義一致。
public class HistoricalWorkOrderGeneratorTests
{
    [Fact]
    public void 同一個種子產生完全一樣的資料()
    {
        // 「評審 clone 下來能訓練出同一個模型」的前提
        var first = new HistoricalWorkOrderGenerator(42).Generate(200);
        var second = new HistoricalWorkOrderGenerator(42).Generate(200);

        Assert.Equal(first, second);
    }

    [Fact]
    public void 不同種子產生不同資料()
    {
        var first = new HistoricalWorkOrderGenerator(1).Generate(200);
        var second = new HistoricalWorkOrderGenerator(2).Generate(200);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void 刻意加進去的缺值真的存在()
    {
        // 缺值是為了讓「資料清理」這一步真的有事可做。
        // 如果生成器其實沒有產生缺值，訓練腳本裡那段補值邏輯就是在處理不存在的問題
        var records = new HistoricalWorkOrderGenerator().Generate(1200);

        Assert.Contains(records, r => r.MaterialReadiness is null);
        Assert.Contains(records, r => r.WeeklyLoadRatio is null);
    }

    [Fact]
    public void 刻意加進去的離群值真的存在()
    {
        var records = new HistoricalWorkOrderGenerator().Generate(1200);

        // 正常負載上限約 1.6，離群值是它的 8~15 倍
        Assert.Contains(records, r => r.WeeklyLoadRatio > 5);
    }

    [Fact]
    public void 兩個類別都有足夠的樣本()
    {
        // 極度不平衡的話，模型只要全猜多數類就有高準確率，評估數字會完全失去意義
        var records = new HistoricalWorkOrderGenerator().Generate(1200);
        var delayed = records.Count(r => r.WasDelayed);

        Assert.InRange(delayed / (double)records.Count, 0.25, 0.75);
    }

    [Fact]
    public void CSV欄位順序與特徵定義一致()
    {
        // 兩邊漂掉時訓練腳本會讀到錯位的欄位，而且不會報錯
        var csv = HistoricalWorkOrderGenerator.ToCsv(new HistoricalWorkOrderGenerator().Generate(3));
        var header = csv.Split('\n')[0].Split(',');

        Assert.Equal("work_order_no", header[0]);
        Assert.Equal("item_code", header[1]);
        Assert.Equal(WorkOrderDelayFeatures.FeatureNames, header[2..^1]);
        Assert.Equal("was_delayed", header[^1]);
    }

    [Fact]
    public void 缺值在CSV裡是空字串而不是哨兵值()
    {
        // 用 0 或 -1 代表缺值的話，模型會把那個數字當成真的量測結果來學
        var records = new HistoricalWorkOrderGenerator().Generate(1200);
        var csv = HistoricalWorkOrderGenerator.ToCsv(records);

        var missingIndex = records.ToList().FindIndex(r => r.MaterialReadiness is null);
        Assert.True(missingIndex >= 0, "這批資料裡沒有缺值，測試前提不成立");

        var columns = csv.Split('\n')[missingIndex + 1].Split(',');
        Assert.Equal("", columns[2]);   // material_readiness 是第三欄
    }
}
