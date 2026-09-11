using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Erp.Infrastructure.Rag;

/// 檢索索引裡的一個文件片段。
///
/// 刻意不放 Erp.Domain —— 它不是領域概念，是檢索用的索引結構。
/// 放進 Domain 會讓 Domain 間接背上「向量是什麼」這個知識，
/// 而 Domain 的唯一規則是不知道任何上層與外部細節的存在。
public sealed class DocumentChunk
{
    public int Id { get; set; }

    /// 來源文件名稱。LLM 回答時要引用它，所以必須是人看得懂的名字而不是檔案路徑
    public string SourceName { get; set; } = string.Empty;

    /// 在該文件中的段落序號，從 0 起。與 SourceName 合起來就是引用座標
    public int ChunkIndex { get; set; }

    public string Text { get; set; } = string.Empty;

    /// 向量，float32 小端序連續排列（見 VectorBlob）
    public byte[] Embedding { get; set; } = [];

    /// 產生這個向量的模型名稱。
    ///
    /// 不是裝飾欄位：換模型後向量空間整個不同，拿舊向量跟新 query 向量算 cosine
    /// 會得到一個「有數字但沒有意義」的結果。查詢時必須比對，不一致就視為索引失效。
    public string EmbeddingModel { get; set; } = string.Empty;

    public int Dimension { get; set; }
}

public sealed class DocumentChunkConfiguration : IEntityTypeConfiguration<DocumentChunk>
{
    public void Configure(EntityTypeBuilder<DocumentChunk> builder)
    {
        builder.ToTable("document_chunks");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.SourceName).HasMaxLength(200).IsRequired();
        builder.Property(c => c.Text).IsRequired();
        builder.Property(c => c.Embedding).IsRequired();
        builder.Property(c => c.EmbeddingModel).HasMaxLength(100).IsRequired();

        // 重建索引時靠這個唯一鍵擋重複灌入，與 ErpDbSeeder 的併發容忍同一個思路
        builder.HasIndex(c => new { c.SourceName, c.ChunkIndex }).IsUnique();
    }
}
