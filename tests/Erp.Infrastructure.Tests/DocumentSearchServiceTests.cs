using Erp.Infrastructure.Rag;
using Microsoft.EntityFrameworkCore;

namespace Erp.Infrastructure.Tests;

/// 檢索服務的行為，重點在防幻覺那幾條：
/// 低於門檻的片段不可以流到 LLM 面前，引用座標必須對得上資料表，
/// 「索引沒建」與「查不到」必須是兩個不同的回答。
public class DocumentSearchServiceTests : IAsyncLifetime
{
    private SqliteTestDatabase _fixture = null!;
    private FakeEmbeddingClient _embedding = null!;

    public async Task InitializeAsync()
    {
        _fixture = new SqliteTestDatabase();
        _embedding = new FakeEmbeddingClient();
        await TestServices.BuildRagIndexAsync(_fixture, _embedding);
    }

    public async Task DisposeAsync() => await _fixture.DisposeAsync();

    private DocumentSearchService CreateService(RagOptions? options = null, FakeEmbeddingClient? embedding = null)
        => TestServices.CreateSearchService(_fixture, embedding ?? _embedding, options);

    [Fact]
    public async Task 查詢字串與某段落完全相同時該段排第一且引用座標對得上資料表()
    {
        // 這條不依賴 embedding 的語意品質：同一段文字必然得到同一個向量，
        // 相似度必然是 1。它驗的是 BLOB round-trip、評分、排序、引用座標整條鏈路
        var target = await _fixture.CreateContext().Set<DocumentChunk>()
            .AsNoTracking().OrderBy(c => c.Id).Skip(5).FirstAsync();

        var result = await CreateService().SearchAsync(target.Text, topK: 3);

        Assert.NotEmpty(result.Chunks);
        Assert.Equal(target.Text, result.Chunks[0].Text);
        Assert.Equal(target.SourceName, result.Chunks[0].SourceName);
        Assert.Equal(target.ChunkIndex, result.Chunks[0].ChunkIndex);
        Assert.Equal(1.0, result.Chunks[0].Similarity, 1e-6);

        // 引用座標不能是憑空生成的：回傳的每一組 (source_name, chunk_index) 都要在索引裡
        var index = await _fixture.CreateContext().Set<DocumentChunk>().AsNoTracking()
            .Select(c => new { c.SourceName, c.ChunkIndex }).ToListAsync();
        Assert.All(result.Chunks, excerpt =>
            Assert.Contains(index, i => i.SourceName == excerpt.SourceName && i.ChunkIndex == excerpt.ChunkIndex));
    }

    [Fact]
    public async Task 相似度由高到低排序()
    {
        var result = await CreateService().SearchAsync("面板色偏的判定方式與處置流程", topK: 5);

        var scores = result.Chunks.Select(c => c.Similarity).ToList();
        Assert.Equal(scores.OrderByDescending(s => s), scores);
    }

    [Fact]
    public async Task topK限制回傳段數()
    {
        var result = await CreateService().SearchAsync("面板色偏", topK: 2);

        Assert.True(result.Chunks.Count <= 2);
        Assert.Equal(result.Chunks.Count, result.MatchedCount);
    }

    [Fact]
    public async Task 不給topK時用預設值()
    {
        var result = await CreateService(new RagOptions { DefaultTopK = 1, SimilarityThreshold = 0 })
            .SearchAsync("面板色偏");

        Assert.Single(result.Chunks);
    }

    [Fact]
    public async Task topK大於命中數時不補湊()
    {
        // 門檻訂得極高，只有「與查詢完全相同」的那一段會過關。
        // 不用 1.0：完全相同的向量算出來是 0.9999999999…，浮點誤差會讓門檻把它也濾掉
        var target = await _fixture.CreateContext().Set<DocumentChunk>().AsNoTracking().FirstAsync();
        var result = await CreateService(new RagOptions { SimilarityThreshold = 0.999 })
            .SearchAsync(target.Text, topK: 10);

        Assert.Single(result.Chunks);
        Assert.Equal(1, result.MatchedCount);
    }

    [Fact]
    public async Task 全部低於門檻時回空清單而不是低分片段()
    {
        // 讓 query 向量與所有片段正交 —— 索引向量都是非負的字元計數，
        // 全負的 query 向量與它們的 cosine 必定 ≤ 0
        var embedding = new FakeEmbeddingClient { Override = _ => [.. Enumerable.Repeat(-1f, 64)] };
        var result = await CreateService(embedding: embedding).SearchAsync("完全無關的主題");

        Assert.Empty(result.Chunks);
        Assert.Equal(0, result.MatchedCount);
        Assert.NotNull(result.Note);
        Assert.Contains("沒有找到", result.Note);
    }

    [Fact]
    public async Task 回傳值帶著採用的門檻讓行為可追()
    {
        var result = await CreateService(new RagOptions { SimilarityThreshold = 0.42 })
            .SearchAsync("面板色偏");

        Assert.Equal(0.42, result.SimilarityThreshold);
    }

    [Fact]
    public async Task 有命中時不附上查不到的說明()
    {
        var target = await _fixture.CreateContext().Set<DocumentChunk>().AsNoTracking().FirstAsync();

        var result = await CreateService().SearchAsync(target.Text);

        Assert.NotEmpty(result.Chunks);
        Assert.Null(result.Note);
    }

    [Fact]
    public async Task 索引為空時回服務不可用而不是空結果()
    {
        // 「索引沒建起來」與「語料裡沒這個主題」是兩件事。
        // 回空結果會讓使用者以為文件裡真的沒有寫
        await using var empty = new SqliteTestDatabase();
        var service = TestServices.CreateSearchService(empty, _embedding);

        var ex = await Assert.ThrowsAsync<EmbeddingUnavailableException>(
            () => service.SearchAsync("面板色偏"));

        Assert.Contains("索引尚未建立", ex.Message);
        Assert.Contains("ollama pull", ex.Message);
    }

    [Fact]
    public async Task 索引模型與設定不符時報錯且不浪費一次embedding呼叫()
    {
        var other = new FakeEmbeddingClient(modelName: "another-model");
        var service = CreateService(embedding: other);

        var ex = await Assert.ThrowsAsync<EmbeddingUnavailableException>(
            () => service.SearchAsync("面板色偏"));

        Assert.Contains("向量空間不同", ex.Message);
        Assert.Equal(0, other.CallCount);
    }

    [Fact]
    public async Task 維度不一致時報錯而不是比較前N維()
    {
        // 同一個模型名稱但維度變了（模型被重新訓練或設定被改），
        // 硬算會得到一個有數字但沒有意義的分數
        var wrongDimension = new FakeEmbeddingClient { Override = _ => new float[8] };
        var service = CreateService(embedding: wrongDimension);

        var ex = await Assert.ThrowsAsync<EmbeddingUnavailableException>(
            () => service.SearchAsync("面板色偏"));

        Assert.Contains("維度不一致", ex.Message);
    }

    [Fact]
    public async Task 查詢內容為空時擲參數例外()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => CreateService().SearchAsync("  "));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(11)]
    public async Task topK超出範圍時擲參數例外(int topK)
    {
        // 沒有上限的話 LLM 可以一次把整個語料庫要回去，那不是檢索
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => CreateService().SearchAsync("面板色偏", topK));
    }

    [Fact]
    public async Task 稽核log記下命中段數與最高相似度()
    {
        var logger = new CapturingLogger<DocumentSearchService>();
        var service = TestServices.CreateSearchService(_fixture, _embedding, null, logger);
        var target = await _fixture.CreateContext().Set<DocumentChunk>().AsNoTracking().FirstAsync();

        await service.SearchAsync(target.Text, topK: 2);

        var entry = Assert.Single(logger.Entries);
        Assert.Contains("命中", entry.Message);
        Assert.Contains("最高相似度 1.0000", entry.Message);
    }

    [Fact]
    public async Task 查不到時log仍留下最高相似度作為門檻診斷依據()
    {
        // 「它說查不到」之後要判斷是語料真的沒有、還是門檻調太高，只有這個數字能回答。
        // 這個數字刻意不回給 LLM，只留在伺服器 log
        var logger = new CapturingLogger<DocumentSearchService>();
        var service = TestServices.CreateSearchService(
            _fixture, _embedding, new RagOptions { SimilarityThreshold = 1.0 }, logger);

        var result = await service.SearchAsync("面板色偏");

        Assert.Empty(result.Chunks);
        var entry = Assert.Single(logger.Entries);
        Assert.Contains("命中 0 段", entry.Message);
        Assert.DoesNotContain("最高相似度 0.0000", entry.Message);
    }
}
