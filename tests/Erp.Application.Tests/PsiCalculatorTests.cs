using Erp.Application.Ml;

namespace Erp.Application.Tests;

/// PSI 漂移偵測的計算邏輯（S5）。
///
/// 用手算得出精確答案的案例釘住公式本身，而不是只驗「數字看起來合理」——
/// PSI 用錯公式最常見的失效方式是「算得出一個數字，但那個數字沒有意義」，
/// 手算案例才抓得到這種錯。
public class PsiCalculatorTests
{
    // 10 等頻分位數的 9 個切點，跟 ml/train.py 的 drift_profile 語意一致
    private static readonly double[] Edges = [1, 2, 3, 4, 5, 6, 7, 8, 9];

    // 均勻分布：10 箱各 1/10，對應「這個特徵是連續值、樣本數夠多」的一般情境
    private static readonly double[] UniformProportions =
        [0.1, 0.1, 0.1, 0.1, 0.1, 0.1, 0.1, 0.1, 0.1, 0.1];

    [Fact]
    public void 最近觀察值完全落在訓練的均勻分布上時_PSI趨近於零()
    {
        // 每一箱剛好一筆，跟訓練時的 10 等頻分箱定義（每箱 1/10）完全吻合
        double[] recent = [0.5, 1.5, 2.5, 3.5, 4.5, 5.5, 6.5, 7.5, 8.5, 9.5];

        var psi = PsiCalculator.Compute(Edges, UniformProportions, recent);

        Assert.True(psi < 1e-6, $"分布完全吻合時 PSI 應該是 0，實際是 {psi}");
    }

    /// 手算對照：全部 100 筆都落在第一箱，其餘 9 箱各是 0 筆。
    /// actual(第一箱) = 1.0，其餘箱用下限 1e-4 夾住。
    /// PSI = (1.0-0.1)ln(1.0/0.1) + 9×(1e-4-0.1)ln(1e-4/0.1) ≈ 8.283
    [Fact]
    public void 全部觀察值擠在同一箱時_PSI是手算出來的大幅飄移數值()
    {
        var recent = Enumerable.Repeat(0.5, 100).ToArray(); // 全部落在第一箱（≤1）

        var psi = PsiCalculator.Compute(Edges, UniformProportions, recent);

        Assert.Equal(8.283, psi, tolerance: 0.01);
        Assert.True(psi > PsiCalculator.SignificantThreshold,
            "全部觀察值擠在同一箱，這是最極端的飄移情境，理應遠超過顯著飄移門檻");
    }

    [Fact]
    public void 反向驗證_輕微偏移落在中度門檻附近_明顯偏移超過顯著門檻()
    {
        // 中度：10 筆裡有 6 筆落在同一箱（相對均勻分布偏移，但不到全部擠在一起）
        double[] moderate = [0.5, 0.5, 0.5, 0.5, 0.5, 0.5, 3.5, 5.5, 7.5, 9.5];
        var moderatePsi = PsiCalculator.Compute(Edges, UniformProportions, moderate);

        // 顯著：全部擠在同一箱
        var severe = Enumerable.Repeat(0.5, 10).ToArray();
        var severePsi = PsiCalculator.Compute(Edges, UniformProportions, severe);

        Assert.True(moderatePsi > PsiCalculator.ModerateThreshold,
            $"6/10 擠在同一箱的偏移量 {moderatePsi} 應該超過中度門檻");
        Assert.True(severePsi > moderatePsi,
            "全部擠在同一箱的飄移程度必須比只有六成擠在一起更嚴重，不然門檻的排序就沒有意義");
    }

    /// 這是寫這個功能時撞到的真實案例（item_overdue_rate 全資料集只有 5 種不同值）：
    /// 訓練集本身在各箱的比例不是均勻的 1/10 時，expected 要用訓練實際比例，
    /// 不能硬套均勻分布——不然分布完全沒變的資料會被誤判成飄移。
    [Fact]
    public void expected比例不是均勻分布時_觀察值符合訓練比例仍判定為沒有飄移()
    {
        double[] edges = [1]; // 只有一個切點 → 兩箱
        double[] trainingProportions = [0.9, 0.1]; // 訓練集九成落在第一箱

        // 最近觀察值維持同樣 9:1 的比例
        double[] recentSameShape = [0, 0, 0, 0, 0, 0, 0, 0, 0, 2];
        var psi = PsiCalculator.Compute(edges, trainingProportions, recentSameShape);

        Assert.True(psi < 1e-6, $"分布形狀跟訓練一致（都是 9:1）時 PSI 應該是 0，實際是 {psi}");
    }

    [Fact]
    public void expectedProportions長度不對時擲例外()
    {
        Assert.Throws<ArgumentException>(
            () => PsiCalculator.Compute(Edges, [0.5, 0.5], [1.0]));
    }

    [Fact]
    public void 沒有觀察值時擲例外_而不是回傳看起來像數字的零()
    {
        Assert.Throws<ArgumentException>(() => PsiCalculator.Compute(Edges, UniformProportions, []));
    }
}
