namespace Erp.Infrastructure.Rag;

/// brute-force cosine similarity。
///
/// 沒有引入 SIMD 套件：語料是數十個片段，768 維 × 30 段約兩萬次乘加，
/// 比一次 HTTP 往返便宜好幾個數量級。瓶頸在 embedding 的網路往返，不在這裡。
public static class VectorMath
{
    /// 餘弦相似度，值域 [-1, 1]。
    ///
    /// 累加一律用 double：float 累加 768 項在最壞情況下會累積到影響排序的誤差，
    /// 而這個函式的輸出就是排序依據。
    public static double CosineSimilarity(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        if (left.IsEmpty || right.IsEmpty)
        {
            throw new ArgumentException("向量不可為空");
        }

        if (left.Length != right.Length)
        {
            // 靜默比較前 N 維會得到一個看起來合理的分數，那比擲例外危險得多：
            // 維度不一致代表索引與查詢用的模型不同，算出來的任何數字都沒有意義
            throw new ArgumentException(
                $"向量維度不一致：{left.Length} 與 {right.Length}");
        }

        double dot = 0, leftNorm = 0, rightNorm = 0;
        for (var i = 0; i < left.Length; i++)
        {
            double l = left[i], r = right[i];
            dot += l * r;
            leftNorm += l * l;
            rightNorm += r * r;
        }

        // 零向量沒有方向，餘弦無定義。回 0（視為不相似）而不是 NaN ——
        // NaN 會讓排序行為變成未定義，而且一路往上傳到回給 LLM 的 JSON 裡
        if (leftNorm <= 0 || rightNorm <= 0)
        {
            return 0;
        }

        return dot / (Math.Sqrt(leftNorm) * Math.Sqrt(rightNorm));
    }
}
