using Erp.Infrastructure.Rag;

namespace Erp.Infrastructure.Tests;

/// brute-force cosine similarity 的邊界行為。
///
/// 這個函式的輸出就是檢索的排序依據，算錯不會拋例外 ——
/// 只會讓「最相關的段落」靜靜地變成另一段。所以邊界值要一條一條釘住。
public class VectorMathTests
{
    private const double Tolerance = 1e-9;

    [Fact]
    public void 相同向量的相似度是1()
    {
        float[] vector = [1f, 2f, 3f, 4f];

        Assert.Equal(1.0, VectorMath.CosineSimilarity(vector, vector), Tolerance);
    }

    [Fact]
    public void 正交向量的相似度是0()
    {
        Assert.Equal(0.0, VectorMath.CosineSimilarity([1f, 0f], [0f, 1f]), Tolerance);
    }

    [Fact]
    public void 反向向量的相似度是負1()
    {
        Assert.Equal(-1.0, VectorMath.CosineSimilarity([1f, 2f], [-1f, -2f]), Tolerance);
    }

    [Fact]
    public void 等比例放大的向量相似度仍是1()
    {
        // 這條是「有沒有真的除以模長」的唯一證據：
        // 少了正規化，內積會隨長度放大，這裡就不會是 1
        Assert.Equal(1.0, VectorMath.CosineSimilarity([1f, 2f, 3f], [100f, 200f, 300f]), Tolerance);
    }

    [Fact]
    public void 零向量回0而不是NaN()
    {
        // NaN 會讓排序變成未定義行為，而且會一路傳到回給 LLM 的 JSON 裡
        var result = VectorMath.CosineSimilarity([0f, 0f, 0f], [1f, 2f, 3f]);

        Assert.False(double.IsNaN(result));
        Assert.Equal(0.0, result);
    }

    [Fact]
    public void 兩個零向量也回0()
    {
        Assert.Equal(0.0, VectorMath.CosineSimilarity([0f, 0f], [0f, 0f]));
    }

    [Fact]
    public void 維度不一致時擲例外而不是比較前N維()
    {
        // 靜默比較前 N 維會回一個看起來合理的分數，比擲例外危險得多
        var ex = Assert.Throws<ArgumentException>(
            () => VectorMath.CosineSimilarity([1f, 2f, 3f], [1f, 2f]));

        Assert.Contains("維度不一致", ex.Message);
    }

    [Fact]
    public void 空向量擲例外()
    {
        Assert.Throws<ArgumentException>(() => VectorMath.CosineSimilarity([], [1f]));
        Assert.Throws<ArgumentException>(() => VectorMath.CosineSimilarity([1f], []));
    }

    [Fact]
    public void 單維向量可正常計算()
    {
        Assert.Equal(1.0, VectorMath.CosineSimilarity([5f], [3f]), Tolerance);
        Assert.Equal(-1.0, VectorMath.CosineSimilarity([5f], [-3f]), Tolerance);
    }

    [Fact]
    public void 極小值不溢位也不回NaN()
    {
        // float 的非正規數範圍，平方後在 float 下會變 0；用 double 累加才不會
        var result = VectorMath.CosineSimilarity([1e-30f, 2e-30f], [1e-30f, 2e-30f]);

        Assert.False(double.IsNaN(result));
        Assert.Equal(1.0, result, 1e-6);
    }

    [Fact]
    public void 已知向量組的排序結果與手算一致()
    {
        float[] query = [1f, 0f, 0f];
        (string Name, float[] Vector)[] candidates =
        [
            ("正交", [0f, 1f, 0f]),            // 0
            ("完全相同", [1f, 0f, 0f]),         // 1
            ("四十五度", [1f, 1f, 0f]),         // 1/√2 ≈ 0.7071
            ("反向", [-1f, 0f, 0f])             // -1
        ];

        var ranked = candidates
            .Select(c => (c.Name, Score: VectorMath.CosineSimilarity(c.Vector, query)))
            .OrderByDescending(x => x.Score)
            .Select(x => x.Name)
            .ToArray();

        Assert.Equal(["完全相同", "四十五度", "正交", "反向"], ranked);
        Assert.Equal(
            1 / Math.Sqrt(2),
            VectorMath.CosineSimilarity(candidates[2].Vector, query),
            Tolerance);
    }
}
