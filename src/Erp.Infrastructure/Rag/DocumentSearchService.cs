using System.Diagnostics;
using Erp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Erp.Infrastructure.Rag;

/// 語意檢索：把問題轉成向量，跟索引裡每個片段算 cosine，取最相近的幾段。
///
/// 全表掃描。語料是數十個片段，載入全部向量再算兩萬次乘加比一次 HTTP 往返便宜得多；
/// 數萬份文件才需要 ANN 索引，那時要換的是這個類別而不是整個架構。
public sealed class DocumentSearchService(
    ErpDbContext db,
    IEmbeddingClient embeddingClient,
    IOptions<RagOptions> options,
    ILogger<DocumentSearchService> logger)
{
    private readonly RagOptions _options = options.Value;

    public async Task<DocumentSearchResult> SearchAsync(
        string query, int? topK = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ArgumentException("查詢內容不可為空", nameof(query));
        }

        var limit = topK ?? _options.DefaultTopK;
        if (limit < 1 || limit > _options.MaxTopK)
        {
            throw new ArgumentOutOfRangeException(
                nameof(topK), limit, $"top_k 必須在 1 到 {_options.MaxTopK} 之間");
        }

        var stopwatch = Stopwatch.StartNew();

        var chunks = await db.Set<DocumentChunk>().AsNoTracking().ToListAsync(ct);
        if (chunks.Count == 0)
        {
            // 「索引是空的」與「查不到相關段落」是兩件不同的事。
            // 回空結果會讓使用者以為語料裡沒有這個主題，而真相是索引從來沒建起來
            throw new EmbeddingUnavailableException(
                "文件檢索索引尚未建立。建立索引需要本機的 Ollama 服務，"
                + $"請確認它已啟動並已執行 ollama pull {embeddingClient.ModelName}，然後重新啟動服務。");
        }

        // 先比對模型再呼叫 embedding：模型不一致時連問都不用問，省一次往返。
        // 不同模型的向量空間不同，硬算會得到一個有數字但沒有意義的相似度
        var indexedModel = chunks[0].EmbeddingModel;
        if (!string.Equals(indexedModel, embeddingClient.ModelName, StringComparison.Ordinal))
        {
            throw new EmbeddingUnavailableException(
                $"索引是以模型 {indexedModel} 建立的，目前設定的模型是 {embeddingClient.ModelName}。"
                + "兩者的向量空間不同，請刪除資料庫後重建索引。");
        }

        var queryVector = await embeddingClient.EmbedAsync(query, ct);

        if (chunks[0].Dimension != queryVector.Length)
        {
            throw new EmbeddingUnavailableException(
                $"索引向量是 {chunks[0].Dimension} 維，查詢向量是 {queryVector.Length} 維，"
                + "維度不一致無法比較，請重建索引。");
        }

        var scored = chunks
            .Select(chunk => new DocumentExcerpt(
                chunk.Text,
                chunk.SourceName,
                chunk.ChunkIndex,
                VectorMath.CosineSimilarity(VectorBlob.FromBlob(chunk.Embedding), queryVector)))
            .OrderByDescending(excerpt => excerpt.Similarity)
            .ToList();

        var matched = scored
            .Where(excerpt => excerpt.Similarity >= _options.SimilarityThreshold)
            .Take(limit)
            .ToList();

        stopwatch.Stop();

        // 檢索層自己記一筆。最高分是過濾「之前」的 ——
        // 「它說查不到」之後要判斷是語料真的沒有，還是門檻調太高，只有這個數字能回答。
        // 這個數字刻意不回給 LLM（決策 D4-A），只留在伺服器 log 裡。
        logger.LogInformation(
            "文件檢索完成，片段總數 {Total}，命中 {Matched} 段，最高相似度 {TopScore:F4}，"
            + "門檻 {Threshold}，耗時 {ElapsedMs} ms",
            chunks.Count, matched.Count, scored.Count > 0 ? scored[0].Similarity : 0,
            _options.SimilarityThreshold, stopwatch.ElapsedMilliseconds);

        return new DocumentSearchResult(
            matched,
            _options.SimilarityThreshold,
            matched.Count,
            matched.Count == 0 ? "沒有找到相似度達到門檻的段落，語料中可能沒有這個主題。" : null);
    }
}
