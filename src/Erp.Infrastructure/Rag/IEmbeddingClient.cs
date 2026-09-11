namespace Erp.Infrastructure.Rag;

/// 把文字轉成向量。
///
/// 這個介面存在的真正理由不是「將來換供應商」，是**測試要能離線跑**：
/// CI 上沒有 Ollama，ToolCatalogConsistencyTests 會走訪每個工具並斷言它不回錯誤，
/// 沒有可替換的假實作，新工具必然讓那組測試變紅。
public interface IEmbeddingClient
{
    /// 產生向量的模型名稱。寫進索引，查詢時比對，不一致就視為索引失效
    string ModelName { get; }

    /// <exception cref="EmbeddingUnavailableException">服務不可用、模型不存在、逾時</exception>
    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);
}
