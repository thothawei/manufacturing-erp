namespace Erp.Infrastructure.Rag;

public sealed class RagOptions
{
    public const string SectionName = "Rag";

    /// 本機 Ollama 的位址。沒裝或沒啟動時 search_documents 會回 SERVICE_UNAVAILABLE，
    /// 核心 ERP 與其他八個工具完全不受影響
    public string OllamaBaseUrl { get; set; } = "http://localhost:11434";

    /// embedding 模型。換模型後必須重建索引 —— 維度與向量空間都不同，
    /// 舊向量與新 query 向量算出的相似度沒有意義（由 DocumentChunk.EmbeddingModel 把關）
    public string EmbeddingModel { get; set; } = "nomic-embed-text";

    /// 單次 embedding 呼叫的逾時秒數。HttpClient 預設 100 秒，
    /// 對互動式查詢來說使用者早就放棄了，但請求還掛在那裡
    public int TimeoutSeconds { get; set; } = 10;

    /// 相似度門檻。低於這個值的片段不會回給 LLM ——
    /// 讓 LLM 自己看分數決定「這段算不算相關」，等於把判準交給無法測試的一方。
    ///
    /// 寫成設定而不是寫死：這是唯一需要看實際語料與模型微調的參數。
    /// nomic-embed-text 的相似度分布偏高，不相關的文字也常落在 0.3–0.5。
    public double SimilarityThreshold { get; set; } = 0.5;

    public int DefaultTopK { get; set; } = 3;

    /// top_k 上限。沒有上限的話，LLM 可以一次把整個語料庫要回去，
    /// 那就不是檢索而是把文件全文塞進 context
    public int MaxTopK { get; set; } = 10;
}
