namespace Erp.Infrastructure.Rag;

/// 一個檢索命中的片段。
///
/// source_name 與 chunk_index 是引用座標：LLM 回答時必須引用這兩個欄位的值，
/// 不能自己生成來源名稱。等價於既有的「所有數字由後端算好」——
/// 這裡由後端算好的是「這段話出自哪裡」。
public sealed record DocumentExcerpt(
    string Text,
    string SourceName,
    int ChunkIndex,
    double Similarity);

/// 檢索結果。
///
/// 低於門檻的片段不會出現在 Chunks 裡（決策 D4-A）：
/// 讓 LLM 看到低分片段，它就可能「參考一下」，那正是幻覺的入口。
public sealed record DocumentSearchResult(
    IReadOnlyList<DocumentExcerpt> Chunks,
    double SimilarityThreshold,
    int MatchedCount,
    string? Note);
