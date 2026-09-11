using Erp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Erp.Infrastructure.Rag;

/// 建立檢索索引：切段、產生向量、寫入 document_chunks。
///
/// 刻意不放進 ErpDbSeeder：那會讓 Persistence 相依於 Rag，
/// 把兩個平行子系統綁成上下關係（架構測試釘住這個方向）。
/// 由 Api 層在 seeding 之後呼叫，Api 本來就依賴兩者。
public sealed class RagIndexBuilder(
    ErpDbContext db,
    IEmbeddingClient embeddingClient,
    ILogger<RagIndexBuilder> logger)
{
    /// 回傳索引裡的片段數。Ollama 不可用時回 0 並只記一條 Warning ——
    /// RAG 是可選模組，它建不起來不該讓整個服務啟動失敗。
    public async Task<int> BuildAsync(CancellationToken ct = default)
    {
        var existing = await db.Set<DocumentChunk>().AsNoTracking()
            .Select(c => c.EmbeddingModel).ToListAsync(ct);

        if (existing.Count > 0)
        {
            if (existing.All(model => model == embeddingClient.ModelName))
            {
                return existing.Count;   // 已是當前模型建的索引，可安全重複呼叫
            }

            // 換過模型。舊向量與新 query 向量不在同一個空間，留著只會算出無意義的分數
            logger.LogInformation(
                "偵測到索引模型與設定不符，清除舊索引後以 {Model} 重建", embeddingClient.ModelName);
            await db.Set<DocumentChunk>().ExecuteDeleteAsync(ct);
        }

        var chunker = new DocumentChunker();
        var pending = new List<DocumentChunk>();

        try
        {
            foreach (var document in DemoCorpus.All)
            {
                var pieces = chunker.Split(document.Text);
                for (var index = 0; index < pieces.Count; index++)
                {
                    var vector = await embeddingClient.EmbedAsync(pieces[index], ct);
                    pending.Add(new DocumentChunk
                    {
                        SourceName = document.SourceName,
                        ChunkIndex = index,
                        Text = pieces[index],
                        Embedding = VectorBlob.ToBlob(vector),
                        EmbeddingModel = embeddingClient.ModelName,
                        Dimension = vector.Length
                    });
                }
            }
        }
        catch (EmbeddingUnavailableException ex)
        {
            // 沒裝 Ollama 是預期情境，不是錯誤。核心 ERP 與既有八個工具完全不受影響，
            // 只有 search_documents 會回 SERVICE_UNAVAILABLE
            logger.LogWarning(
                "文件檢索索引未建立：{Reason} 核心 ERP 與既有工具不受影響，"
                + "search_documents 會回報服務不可用。", ex.Message);
            return 0;
        }

        db.Set<DocumentChunk>().AddRange(pending);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // 多個實例同時啟動時會撞上 (source_name, chunk_index) 的唯一鍵，
            // 與 ErpDbSeeder 同一個競態、同一個處理方式：確認資料確實在了就當作成功
            db.ChangeTracker.Clear();
            var count = await db.Set<DocumentChunk>().CountAsync(ct);
            if (count == 0)
            {
                throw;
            }

            logger.LogInformation("索引已由其他實例建立，共 {Count} 段", count);
            return count;
        }

        logger.LogInformation(
            "文件檢索索引建立完成：{Documents} 份文件、{Chunks} 段、模型 {Model}",
            DemoCorpus.All.Count, pending.Count, embeddingClient.ModelName);

        return pending.Count;
    }
}
