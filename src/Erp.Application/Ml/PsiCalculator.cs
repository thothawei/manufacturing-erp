namespace Erp.Application.Ml;

/// Population Stability Index：量「最近觀察到的一批值」跟「訓練分布」飄了多遠（S5 漂移偵測）。
///
/// 分箱邊界在訓練時用 10 等頻分位數切出來，但**不能假設每箱一定是 1/10**：
/// 低基數的類別型特徵（例如 item_overdue_rate 全資料集只有 5 種不同值）九個分位數
/// 切點會切出重複值，這時候訓練腳本會先去重邊界、再對訓練集重新算一次真正的比例
/// （見 ml/train.py 的 drift_profile）——這裡收到的 expectedProportions 就是那個
/// 已經算好的實際比例，不是假設出來的均勻分布。這是寫測試時撞到的真實案例，
/// 不是預想出來的邊界情況：把它當成均勻分布會讓完全正常的資料被誤判成飄移。
///
/// 業界慣用門檻（PSI 本身沒有單位，是經驗法則）：
///   &lt; 0.1  沒有顯著變化
///   0.1~0.2  中度飄移，值得留意
///   ≥ 0.2   顯著飄移，訓練分布可能已經不能代表現在的輸入
public static class PsiCalculator
{
    public const double ModerateThreshold = 0.1;
    public const double SignificantThreshold = 0.2;

    /// 分箱後某一箱一筆都沒有時，直接用 0 算 ln 會是負無限大。
    /// 用一個很小的下限夾住，讓那一箱貢獻一個很大但有限的 PSI 增量——
    /// 「這一箱完全沒有樣本」本身就是強烈的飄移訊號，不該被夾成 0 而消失。
    private const double MinProportion = 1e-4;

    /// binEdges：訓練時算出的分箱邊界（去重過，由小到大排序）。
    /// expectedProportions：訓練集在每一箱的實際比例，長度必須是 binEdges.Count + 1。
    /// recentValues：最近觀察到的值，用同一組邊界分箱後跟 expectedProportions 比對。
    public static double Compute(
        IReadOnlyList<double> binEdges,
        IReadOnlyList<double> expectedProportions,
        IReadOnlyList<double> recentValues)
    {
        if (binEdges.Count == 0)
        {
            throw new ArgumentException("需要至少一個分箱邊界", nameof(binEdges));
        }

        if (expectedProportions.Count != binEdges.Count + 1)
        {
            throw new ArgumentException(
                $"expectedProportions 長度應為 {binEdges.Count + 1}（binEdges.Count + 1），"
                + $"實際是 {expectedProportions.Count}",
                nameof(expectedProportions));
        }

        if (recentValues.Count == 0)
        {
            throw new ArgumentException("沒有觀察值可以算 PSI", nameof(recentValues));
        }

        var binCount = binEdges.Count + 1;
        var counts = new int[binCount];

        foreach (var value in recentValues)
        {
            counts[BucketOf(binEdges, value)]++;
        }

        var psi = 0.0;

        for (var bin = 0; bin < binCount; bin++)
        {
            var expected = Math.Max(MinProportion, expectedProportions[bin]);
            var actual = Math.Max(MinProportion, counts[bin] / (double)recentValues.Count);
            psi += (actual - expected) * Math.Log(actual / expected);
        }

        return psi;
    }

    /// 半開區間 (edge[i-1], edge[i]]，跟訓練那邊 numpy.quantile 切出來的邊界語意一致
    private static int BucketOf(IReadOnlyList<double> edges, double value)
    {
        var bin = 0;
        while (bin < edges.Count && value > edges[bin])
        {
            bin++;
        }

        return bin;
    }
}
