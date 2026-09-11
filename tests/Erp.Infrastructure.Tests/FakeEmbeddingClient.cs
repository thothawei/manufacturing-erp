using Erp.Infrastructure.Rag;

namespace Erp.Infrastructure.Tests;

/// 離線可跑的假 embedding。
///
/// 這個類別是 IEmbeddingClient 存在的真正理由：CI 上沒有 Ollama，
/// 而 ToolCatalogConsistencyTests 會走訪每個工具並斷言它不回錯誤 ——
/// 沒有可替換的假實作，第九個工具必然讓那組測試變紅。
///
/// 預設實作是「字元雜湊袋」：把每個字元投進固定數量的 bucket 計數。
/// 這不是語意向量，但它有一個測試需要的性質 —— 共用字元越多、cosine 越高，
/// 所以排序、門檻、top_k 這些行為可以用真實語料驗證，而不必接真模型。
public sealed class FakeEmbeddingClient : IEmbeddingClient
{
    private readonly int _dimension;

    public FakeEmbeddingClient(string modelName = "fake-embed", int dimension = 64)
    {
        ModelName = modelName;
        _dimension = dimension;
    }

    public string ModelName { get; }

    public int CallCount { get; private set; }

    /// 設成非 null 時，每次呼叫都擲這個例外，用來模擬 Ollama 不可用
    public EmbeddingUnavailableException? Failure { get; set; }

    /// 需要精確控制向量時覆寫（例如要讓某段刻意落在門檻以下）
    public Func<string, float[]>? Override { get; set; }

    public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        CallCount++;

        if (Failure is not null)
        {
            throw Failure;
        }

        if (Override is not null)
        {
            return Task.FromResult(Override(text));
        }

        var vector = new float[_dimension];
        foreach (var ch in text)
        {
            vector[ch % _dimension] += 1f;
        }

        return Task.FromResult(vector);
    }
}
