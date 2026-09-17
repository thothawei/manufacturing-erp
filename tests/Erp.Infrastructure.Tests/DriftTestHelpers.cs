using Erp.Infrastructure.Ml;

namespace Erp.Infrastructure.Tests;

/// 漂移偵測測試共用的小工具，DelayRiskModelTests 與 MaterialDemandForecastModelTests
/// 都用得到，避免兩邊各自維護一份一樣的分箱構造邏輯。
internal static class DriftTestHelpers
{
    /// 依 profile.Proportions 的比例，用 bin 中點造出一批完全複製那個比例的觀察值——
    /// 用來證明「觀察值分布跟訓練一致時 PSI 應該趨近於零」。
    public static List<double> ReproduceProportions(DriftProfile profile)
    {
        const int scale = 10_000;
        var midpoints = BinMidpoints(profile.Edges);
        var values = new List<double>();

        for (var bin = 0; bin < profile.Proportions.Count; bin++)
        {
            var count = (int)Math.Round(profile.Proportions[bin] * scale);
            values.AddRange(Enumerable.Repeat(midpoints[bin], count));
        }

        return values;
    }

    /// 給 N-1 個等頻分箱邊界，回傳 N 個代表值：每個 bin 各一個，落在該 bin 的中點
    /// （頭尾兩個開區間 bin 各往外推一點點，保證真的落進那個 bin，不是剛好卡在邊界上）
    public static List<double> BinMidpoints(IReadOnlyList<double> edges)
    {
        var span = Math.Max(edges[^1] - edges[0], 1e-6);
        var margin = span * 0.05;

        var points = new List<double> { edges[0] - margin };
        for (var i = 1; i < edges.Count; i++)
        {
            points.Add((edges[i - 1] + edges[i]) / 2);
        }

        points.Add(edges[^1] + margin);
        return points;
    }
}
