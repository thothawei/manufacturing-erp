using Erp.Infrastructure.Rag;

namespace Erp.Infrastructure.Tests;

public class DocumentChunkerTests
{
    [Fact]
    public void 依空行切段()
    {
        var chunks = new DocumentChunker(minChars: 5).Split(
            "第一段的內容寫在這裡。\n\n第二段的內容寫在這裡。\n\n第三段的內容寫在這裡。");

        Assert.Equal(3, chunks.Count);
        Assert.StartsWith("第一段", chunks[0]);
        Assert.StartsWith("第三段", chunks[2]);
    }

    [Fact]
    public void 段落內的單一換行不切段()
    {
        // 條列式的 SOP 一段裡會有多行，那是同一個主題，切開會讓每一行都失去上下文
        var chunks = new DocumentChunker(minChars: 5).Split("判定方式：\n第一步量測。\n第二步比對。");

        Assert.Single(chunks);
    }

    [Fact]
    public void 太短的段落併進下一段()
    {
        // 標題行單獨成段會變成一個幾乎沒有資訊的片段，只會佔掉 top_k 的位置
        var chunks = new DocumentChunker(minChars: 20).Split(
            "標題\n\n這一段的內容長度足夠構成一個有意義的片段。");

        Assert.Single(chunks);
        Assert.StartsWith("標題", chunks[0]);
        Assert.Contains("內容長度足夠", chunks[0]);
    }

    [Fact]
    public void 最後一段太短時併進前一段而不是留下零碎片段()
    {
        var chunks = new DocumentChunker(minChars: 20).Split(
            "這一段的內容長度足夠構成一個有意義的片段。\n\n結尾");

        Assert.Single(chunks);
        Assert.EndsWith("結尾", chunks[0]);
    }

    [Fact]
    public void 過長的段落按句末標點切開()
    {
        var sentence = new string('甲', 30) + "。";
        var chunks = new DocumentChunker(maxChars: 100, minChars: 5).Split(string.Concat(
            Enumerable.Repeat(sentence, 10)));

        Assert.True(chunks.Count > 1, "超過 maxChars 的段落應該被切開");
        Assert.All(chunks, c => Assert.True(c.Length <= 100, $"片段長度 {c.Length} 超過上限"));
    }

    [Fact]
    public void 單一句子超過上限時硬切()
    {
        // 寧可切在奇怪的位置，也不要產生一個超大片段 ——
        // 一個片段講五件事，它對其中任一件的相似度都不高
        var chunks = new DocumentChunker(maxChars: 50, minChars: 5).Split(new string('乙', 180));

        Assert.Equal(4, chunks.Count);
        Assert.All(chunks, c => Assert.True(c.Length <= 50));
    }

    [Fact]
    public void 空白輸入回空清單()
    {
        Assert.Empty(new DocumentChunker().Split(""));
        Assert.Empty(new DocumentChunker().Split("   \n\n  "));
    }

    [Fact]
    public void 片段不含前後空白也不含空片段()
    {
        var chunks = new DocumentChunker(minChars: 5).Split(
            "  第一段有前後空白。  \n\n\n\n  第二段也有。  ");

        Assert.All(chunks, c => Assert.Equal(c, c.Trim()));
        Assert.All(chunks, c => Assert.NotEmpty(c));
    }

    [Fact]
    public void 展示語料每一份都切得出多個片段且長度在上限內()
    {
        // 語料是手寫的，寫成一大段就會得到一個超大片段 ——
        // 檢索會「有回應但不精準」，沒有任何錯誤訊息
        var chunker = new DocumentChunker();

        foreach (var document in DemoCorpus.All)
        {
            var chunks = chunker.Split(document.Text);

            Assert.True(chunks.Count >= 3,
                $"{document.SourceName} 只切出 {chunks.Count} 段，語料應該一個主題一段");
            Assert.All(chunks, c => Assert.True(c.Length <= 400,
                $"{document.SourceName} 有片段長度 {c.Length} 超過 400 字"));
        }
    }

    [Fact]
    public void 展示語料的總量在規劃的量級內()
    {
        // 決策 D1-B：6–8 份文件、約 7000 字、約 30 片段。
        // brute-force 的效能前提就是這個量級，語料無限長大時要先換 ANN 索引
        var chunker = new DocumentChunker();
        var total = DemoCorpus.All.Sum(d => chunker.Split(d.Text).Count);

        Assert.InRange(DemoCorpus.All.Count, 6, 8);
        Assert.InRange(total, 20, 45);
    }
}
