using Erp.Infrastructure.Rag;
using Microsoft.Extensions.Options;

namespace Erp.Infrastructure.Tests;

/// 檢索品質：接**真實 Ollama** 跑，本機沒有 Ollama 時整組 skip。
///
/// 這組測試的存在理由是一個真實事故：第一版預設 `nomic-embed-text`，
/// 255 個測試全綠，但裝上真模型一跑才發現無關查詢（「今天天氣如何」）
/// 拿到 0.59–0.62 分，比真正相關的查詢還高 ——
/// **沒有任何門檻值能分開兩者，防幻覺的第一道防線形同不存在**。
///
/// 用假 embedding 的單元測試驗得了「門檻有沒有被套用」（拔掉會紅 5 條），
/// 驗不了「門檻值有沒有意義」。那是兩件不同的事，需要兩組不同的測試。
///
/// 下面第二組（無關查詢必須回 0 段）就是會抓到那個事故的那一條：
/// 把模型換回 nomic-embed-text，它會紅。
public class RetrievalQualityTests : IAsyncLifetime
{
    private static readonly RagOptions Options = new() { TimeoutSeconds = 120 };

    private SqliteTestDatabase _fixture = null!;
    private OllamaEmbeddingClient _client = null!;

    public async Task InitializeAsync()
    {
        if (OllamaAvailability.Reason is not null)
        {
            return;   // 整組會被 skip，不必花時間建索引
        }

        _fixture = new SqliteTestDatabase();
        _client = new OllamaEmbeddingClient(Microsoft.Extensions.Options.Options.Create(Options));
        await TestServices.BuildRagIndexAsync(_fixture, _client);
    }

    public async Task DisposeAsync()
    {
        if (_fixture is not null)
        {
            await _fixture.DisposeAsync();
        }
    }

    private DocumentSearchService Search(double threshold)
        => TestServices.CreateSearchService(
            _fixture, _client, new RagOptions { TimeoutSeconds = 120, SimilarityThreshold = threshold });

    /// 問題刻意寫成現場會問的樣子（帶具體名詞），而不是抽象的關鍵字。
    /// 斷言用 top-3 而不是 top-1：實際使用是 top_k=3，而 top-1 會隨模型小版本漂動，
    /// 斷言它只會製造脆弱的測試。
    [OllamaTheory]
    [InlineData("面板色偏怎麼判定？允收標準是多少？", "品管異常處理 SOP — 面板色偏")]
    [InlineData("以前有過面板色偏的批量客訴嗎？怎麼處理的？", "客訴處理紀錄 — 面板色偏批量客訴")]
    [InlineData("貼合機加熱板壞了要等多久才有零件？", "設備維修手冊摘要 — 面板貼合機")]
    [InlineData("機殼尺寸超差要退料還是挑選使用？", "品管異常處理 SOP — 外觀尺寸超差")]
    [InlineData("訊號線接觸不良的客訴是怎麼改善的？", "客訴處理紀錄 — 訊號線接觸不良")]
    public async Task 相關查詢的前三段要含正確的來源文件(string query, string expectedSource)
    {
        var result = await Search(Options.SimilarityThreshold).SearchAsync(query, topK: 3);

        Assert.NotEmpty(result.Chunks);
        Assert.Contains(expectedSource, result.Chunks.Select(c => c.SourceName));
    }

    /// **這組是整個檔案的重點。**
    /// 門檻值有沒有意義，等價於「無關的問題會不會被擋下來」。
    /// nomic-embed-text 在這組會紅 —— 它讓天氣與寫程式的問題都拿到 0.59 以上。
    [OllamaTheory]
    [InlineData("今天台北天氣如何？")]
    [InlineData("幫我寫一個 Python 的 for 迴圈")]
    [InlineData("下一季的匯率走勢怎麼看？")]
    public async Task 與語料無關的問題必須回空結果(string query)
    {
        var result = await Search(Options.SimilarityThreshold).SearchAsync(query, topK: 3);

        Assert.Empty(result.Chunks);
        Assert.Equal(0, result.MatchedCount);
        Assert.NotNull(result.Note);
    }

    /// 分離度：相關問題的最高分要明顯高於無關問題的最高分。
    ///
    /// 誠實標註這條的侷限 —— 它**不足以**抓到前述事故：nomic 當時是
    /// 相關 0.648 / 無關 0.622，分離度還是正的，只是小到沒有門檻放得下去。
    /// 真正會紅的是上面那組。這條補的是另一件事：提早警告分離度在縮小
    /// （換模型或語料長歪時），在它還沒小到擋不住之前。
    [OllamaFact]
    public async Task 相關與無關問題的分數要有可用的分離度()
    {
        var service = Search(threshold: 0);   // 門檻設 0，看原始分數

        var relevant = await TopScoreAsync(service, "面板色偏怎麼判定？允收標準是多少？");
        var alsoRelevant = await TopScoreAsync(service, "組裝後有異音，先查什麼？");
        var irrelevant = await TopScoreAsync(service, "今天台北天氣如何？");
        var alsoIrrelevant = await TopScoreAsync(service, "幫我寫一個 Python 的 for 迴圈");

        var lowestRelevant = Math.Min(relevant, alsoRelevant);
        var highestIrrelevant = Math.Max(irrelevant, alsoIrrelevant);

        Assert.True(lowestRelevant - highestIrrelevant >= 0.1,
            $"相關問題最低 {lowestRelevant:F4}、無關問題最高 {highestIrrelevant:F4}，"
            + $"分離度只有 {lowestRelevant - highestIrrelevant:F4}。"
            + $"門檻（目前 {Options.SimilarityThreshold}）必須落在兩者之間才有意義，"
            + "分離度太小代表這個模型對這批語料不適用，換模型而不是調門檻。");

        Assert.InRange(Options.SimilarityThreshold, highestIrrelevant, lowestRelevant);
    }

    private static async Task<double> TopScoreAsync(DocumentSearchService service, string query)
    {
        var result = await service.SearchAsync(query, topK: 1);
        return result.Chunks.Count > 0 ? result.Chunks[0].Similarity : 0;
    }
}
