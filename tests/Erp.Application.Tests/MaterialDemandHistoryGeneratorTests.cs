using Erp.Application.Ml;

namespace Erp.Application.Tests;

/// 模擬週別物料需求歷史生成器（S6）。
///
/// 跟 HistoricalWorkOrderGeneratorTests 驗的是同一類事：可重現、季節性/趨勢真的
/// 有被灌進去（不然三方比較會失去意義——如果資料根本沒有季節性，GBDT/DL
/// 贏不贏 seasonal naive 就只是雜訊），CSV 欄位順序正確。
public class MaterialDemandHistoryGeneratorTests
{
    [Fact]
    public void 同一個種子產生完全一樣的資料()
    {
        var first = new MaterialDemandHistoryGenerator(42).Generate(52);
        var second = new MaterialDemandHistoryGenerator(42).Generate(52);

        Assert.Equal(first, second);
    }

    [Fact]
    public void 不同種子產生不同資料()
    {
        var first = new MaterialDemandHistoryGenerator(1).Generate(52);
        var second = new MaterialDemandHistoryGenerator(2).Generate(52);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void 三個品項各自產生完整的週數()
    {
        var records = new MaterialDemandHistoryGenerator().Generate(156);

        var byItem = records.GroupBy(r => r.ItemCode).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(3, byItem.Count);
        Assert.All(byItem.Values, count => Assert.Equal(156, count));
    }

    [Fact]
    public void 週序號從零開始且連續遞增()
    {
        var records = new MaterialDemandHistoryGenerator().Generate(52);

        foreach (var group in records.GroupBy(r => r.ItemCode))
        {
            var indices = group.Select(r => r.WeekIndex).ToList();
            Assert.Equal(Enumerable.Range(0, 52), indices);
        }
    }

    [Fact]
    public void 週起始日期每週間隔七天()
    {
        var records = new MaterialDemandHistoryGenerator().Generate(10)
            .Where(r => r.ItemCode == "PANEL-01").OrderBy(r => r.WeekIndex).ToList();

        for (var i = 1; i < records.Count; i++)
        {
            Assert.Equal(7, records[i].WeekStart.DayNumber - records[i - 1].WeekStart.DayNumber);
        }
    }

    [Fact]
    public void 需求量真的有季節性起伏_不是一條平線()
    {
        // 這條防的是「振幅參數沒接上」：如果 seasonal_amplitude 沒生效，
        // 每個品項的需求量會幾乎是常數（只剩雜訊），時間序列預測這個題目就沒有意義了
        var records = new MaterialDemandHistoryGenerator().Generate(156)
            .Where(r => r.ItemCode == "PANEL-01" && r.DemandQty is not null)
            .Select(r => r.DemandQty!.Value)
            .ToList();

        var mean = records.Average();
        var stdDev = Math.Sqrt(records.Average(v => Math.Pow(v - mean, 2)));

        // 純雜訊（noise_ratio=8%）的標準差大約是 mean 的 8%；有季節性的話變異會明顯更大
        Assert.True(stdDev / mean > 0.15, $"標準差只有均值的 {stdDev / mean:P1}，季節性看起來沒有生效");
    }

    [Fact]
    public void 刻意加進去的缺值真的存在()
    {
        var records = new MaterialDemandHistoryGenerator().Generate(156);

        Assert.Contains(records, r => r.DemandQty is null);
    }

    [Fact]
    public void 需求量永遠不是負數()
    {
        var records = new MaterialDemandHistoryGenerator().Generate(156);

        Assert.All(records.Where(r => r.DemandQty is not null), r => Assert.True(r.DemandQty >= 0));
    }

    [Fact]
    public void CSV欄位順序正確且缺值是空字串而不是哨兵值()
    {
        var records = new MaterialDemandHistoryGenerator().Generate(52);
        var csv = MaterialDemandHistoryGenerator.ToCsv(records);
        var lines = csv.Split('\n');

        Assert.Equal("item_code,week_index,week_start,demand_qty", lines[0]);

        var missingIndex = records.ToList().FindIndex(r => r.DemandQty is null);
        Assert.True(missingIndex >= 0, "這批資料裡沒有缺值，測試前提不成立");

        var columns = lines[missingIndex + 1].Split(',');
        Assert.Equal("", columns[3]);
    }
}
