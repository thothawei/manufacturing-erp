using Erp.Infrastructure.Rag;

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
/// 題目來自 `RetrievalEvaluationSet`（30 組標註查詢）。原本只有 8 組 ——
/// 那個量級能擋住「換到一個不可用的模型」，擋不住細微退化：少一題命中就掉 12.5 個
/// 百分點，分數本身沒有解析度。擴充後加上 MRR 與 Recall@3 兩個排序指標當迴歸基準。
public class RetrievalQualityTests : IAsyncLifetime
{
    private static readonly RagOptions Options = new() { TimeoutSeconds = 120 };

    /// 基線是 2026-09-13 用 bge-m3 對這批語料實測出來的：
    /// MRR@10 = 0.9206、Recall@3 = 1.00（21 題全部落在前三）、Recall@1 = 0.857。
    /// 門檻設在實測值下方留一題的緩衝 —— 卡在實測值上等於每次模型小版本更新都紅。
    private const double MrrBaseline = 0.85;
    private const double RecallAt3Baseline = 0.95;

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

    private DocumentSearchService Search(double threshold, int maxTopK = 3)
        => TestServices.CreateSearchService(
            _fixture, _client,
            new RagOptions { TimeoutSeconds = 120, SimilarityThreshold = threshold, MaxTopK = maxTopK });

    public static TheoryData<string, string, string?> AnswerableQueries()
    {
        var data = new TheoryData<string, string, string?>();
        foreach (var q in RetrievalEvaluationSet.Answerable)
        {
            data.Add(q.Query, q.ExpectedSource!, q.AlternativeSource);
        }
        return data;
    }

    public static TheoryData<string> IrrelevantQueries()
    {
        var data = new TheoryData<string>();
        foreach (var q in RetrievalEvaluationSet.OfKind(QueryKind.Irrelevant))
        {
            data.Add(q.Query);
        }
        return data;
    }

    /// 斷言用 top-3 而不是 top-1：實際使用是 top_k=3，而 top-1 會隨模型小版本漂動，
    /// 斷言它只會製造脆弱的測試。
    [OllamaTheory]
    [MemberData(nameof(AnswerableQueries))]
    public async Task 相關查詢的前三段要含正確的來源文件(
        string query, string expectedSource, string? alternativeSource)
    {
        var result = await Search(Options.SimilarityThreshold).SearchAsync(query, topK: 3);

        Assert.NotEmpty(result.Chunks);

        var sources = result.Chunks.Select(c => c.SourceName).ToList();
        Assert.True(
            sources.Contains(expectedSource) || (alternativeSource is not null && sources.Contains(alternativeSource)),
            $"「{query}」的前三段是 [{string.Join("、", sources.Distinct())}]，"
            + $"不含應有的來源「{expectedSource}」。");
    }

    /// **這組是整個檔案的重點。**
    /// 門檻值有沒有意義，等價於「無關的問題會不會被擋下來」。
    /// nomic-embed-text 在這組會紅 —— 它讓天氣與寫程式的問題都拿到 0.59 以上。
    [OllamaTheory]
    [MemberData(nameof(IrrelevantQueries))]
    public async Task 與語料無關的問題必須回空結果(string query)
    {
        var result = await Search(Options.SimilarityThreshold).SearchAsync(query, topK: 3);

        Assert.Empty(result.Chunks);
        Assert.Equal(0, result.MatchedCount);
        Assert.NotNull(result.Note);
    }

    /// MRR（Mean Reciprocal Rank）：正確來源第一次出現的名次取倒數再平均。
    /// 全部排第一是 1.0，全部排第二是 0.5。
    ///
    /// 它比「前三段有沒有命中」敏感：正確答案從第 1 名掉到第 3 名，
    /// 命中率完全看不出來，MRR 會從 1.0 掉到 0.33。這正是 8 組題目時抓不到的那種退化。
    [OllamaFact]
    public async Task MRR不得低於基線()
    {
        var service = Search(Options.SimilarityThreshold, maxTopK: 10);

        var reciprocalRanks = new List<(string Query, double Rr)>();

        foreach (var q in RetrievalEvaluationSet.Answerable)
        {
            var result = await service.SearchAsync(q.Query, topK: 10);
            var rank = RankOfExpected(result, q);
            reciprocalRanks.Add((q.Query, rank == 0 ? 0 : 1.0 / rank));
        }

        var mrr = reciprocalRanks.Average(x => x.Rr);
        var worst = reciprocalRanks.Where(x => x.Rr < 1).OrderBy(x => x.Rr).Take(3).ToList();

        Assert.True(mrr >= MrrBaseline,
            $"MRR = {mrr:F4}，低於基線 {MrrBaseline}。排在第一名以外的題目："
            + string.Join("；", worst.Select(w => $"「{w.Query}」(RR={w.Rr:F2})")));
    }

    /// Recall@3：實際使用是 top_k=3，所以「正確來源有沒有進前三」就是使用者實際拿到的品質。
    /// MRR 看排序細緻度，這條看「會不會根本漏掉」，兩者抓的是不同的退化。
    [OllamaFact]
    public async Task Recall_at_3不得低於基線()
    {
        var service = Search(Options.SimilarityThreshold);

        var missed = new List<string>();
        var total = 0;

        foreach (var q in RetrievalEvaluationSet.Answerable)
        {
            total++;
            var result = await service.SearchAsync(q.Query, topK: 3);
            if (RankOfExpected(result, q) == 0)
            {
                missed.Add(q.Query);
            }
        }

        var recall = (total - missed.Count) / (double)total;

        Assert.True(recall >= RecallAt3Baseline,
            $"Recall@3 = {recall:F4}（{total - missed.Count}/{total}），低於基線 {RecallAt3Baseline}。"
            + $"沒命中的題目：{string.Join("；", missed)}");
    }

    /// 分離度：相關問題的最低分要明顯高於無關問題的最高分。
    ///
    /// 誠實標註這條的侷限 —— 它**不足以**抓到前述事故：nomic 當時是
    /// 相關 0.648 / 無關 0.622，分離度還是正的，只是小到沒有門檻放得下去。
    /// 真正會紅的是上面那組。這條補的是另一件事：提早警告分離度在縮小
    /// （換模型或語料長歪時），在它還沒小到擋不住之前。
    ///
    /// 用整份評測集算而不是挑四題：挑題目等於挑一個好看的分離度。
    [OllamaFact]
    public async Task 相關與無關問題的分數要有可用的分離度()
    {
        var service = Search(threshold: 0);   // 門檻設 0，看原始分數

        var lowestRelevant = double.MaxValue;
        var lowestRelevantQuery = "";
        foreach (var q in RetrievalEvaluationSet.Answerable)
        {
            var score = await TopScoreAsync(service, q.Query);
            if (score < lowestRelevant)
            {
                (lowestRelevant, lowestRelevantQuery) = (score, q.Query);
            }
        }

        var highestIrrelevant = 0d;
        var highestIrrelevantQuery = "";
        foreach (var q in RetrievalEvaluationSet.OfKind(QueryKind.Irrelevant))
        {
            var score = await TopScoreAsync(service, q.Query);
            if (score > highestIrrelevant)
            {
                (highestIrrelevant, highestIrrelevantQuery) = (score, q.Query);
            }
        }

        Assert.True(lowestRelevant - highestIrrelevant >= 0.1,
            $"相關問題最低 {lowestRelevant:F4}（「{lowestRelevantQuery}」）、"
            + $"無關問題最高 {highestIrrelevant:F4}（「{highestIrrelevantQuery}」），"
            + $"分離度只有 {lowestRelevant - highestIrrelevant:F4}。"
            + $"門檻（目前 {Options.SimilarityThreshold}）必須落在兩者之間才有意義，"
            + "分離度太小代表這個模型對這批語料不適用，換模型而不是調門檻。");

        Assert.InRange(Options.SimilarityThreshold, highestIrrelevant, lowestRelevant);
    }

    /// **這條釘的是一個已知限制，不是一個能力。**
    ///
    /// 「主題沾得上邊、但語料裡根本沒寫」的問題（售價、付款條件、賠償金額）
    /// 拿到的分數是 0.55–0.63，落在真正相關題目的區間裡（最低 0.5788）——
    /// 相似度門檻分不開這兩者，把門檻拉高到擋得住它們，就會同時擋掉真正相關的問題。
    ///
    /// 所以防線不在這一層，在 system prompt：檢索回來的段落若沒有回答到問題，
    /// 要說文件裡沒寫，不可以從相近段落推測（`SystemPromptTests` 釘住那條規則）。
    ///
    /// 這條測試會在「哪天有模型能分開它們」時變紅 —— 那是好消息，
    /// 到時要做的是回頭更新這段敘述與文件裡的已知限制，而不是把測試刪掉。
    [OllamaFact]
    public async Task 語料沒寫的邊界問題_門檻擋不住_這是已知限制()
    {
        var service = Search(Options.SimilarityThreshold);

        var passed = new List<string>();
        foreach (var q in RetrievalEvaluationSet.OfKind(QueryKind.OutOfScope))
        {
            var result = await service.SearchAsync(q.Query, topK: 3);
            if (result.Chunks.Count > 0)
            {
                passed.Add($"「{q.Query}」({result.Chunks[0].Similarity:F4})");
            }
        }

        Assert.True(passed.Count > 0,
            "語料沒寫的邊界問題全部被門檻擋下來了 —— 這比現況好，"
            + "請回頭更新這條測試的敘述、README 與 rag-module-plan-v1.md 的已知限制。");
    }

    private static int RankOfExpected(DocumentSearchResult result, LabelledQuery query)
    {
        for (var i = 0; i < result.Chunks.Count; i++)
        {
            if (result.Chunks[i].SourceName == query.ExpectedSource
                || (query.AlternativeSource is not null && result.Chunks[i].SourceName == query.AlternativeSource))
            {
                return i + 1;
            }
        }

        return 0;
    }

    private static async Task<double> TopScoreAsync(DocumentSearchService service, string query)
    {
        var result = await service.SearchAsync(query, topK: 1);
        return result.Chunks.Count > 0 ? result.Chunks[0].Similarity : 0;
    }
}
