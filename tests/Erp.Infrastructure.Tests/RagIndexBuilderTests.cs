using Erp.Infrastructure.Rag;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Erp.Infrastructure.Tests;

public class RagIndexBuilderTests : IAsyncLifetime
{
    private SqliteTestDatabase _fixture = null!;

    public Task InitializeAsync()
    {
        _fixture = new SqliteTestDatabase();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _fixture.DisposeAsync();

    private RagIndexBuilder CreateBuilder(
        IEmbeddingClient embedding, ILogger<RagIndexBuilder>? logger = null)
        => new(_fixture.CreateContext(), embedding, logger ?? NullLogger<RagIndexBuilder>.Instance);

    [Fact]
    public async Task 建立索引後每一段都帶著來源模型與維度()
    {
        var embedding = new FakeEmbeddingClient(dimension: 64);

        var count = await CreateBuilder(embedding).BuildAsync();

        Assert.True(count >= 20, $"只建了 {count} 段，語料應該切出二十段以上");
        var chunks = await _fixture.CreateContext().Set<DocumentChunk>().AsNoTracking().ToListAsync();
        Assert.Equal(count, chunks.Count);
        Assert.All(chunks, chunk =>
        {
            Assert.NotEmpty(chunk.Text);
            Assert.Equal("fake-embed", chunk.EmbeddingModel);
            Assert.Equal(64, chunk.Dimension);
            Assert.Equal(64 * 4, chunk.Embedding.Length);
            Assert.Contains(chunk.SourceName, DemoCorpus.All.Select(d => d.SourceName));
        });
    }

    [Fact]
    public async Task 每份文件的段落序號從0連續編到底()
    {
        // chunk_index 是引用座標的一半，跳號或重號會讓引用對不上原文
        await CreateBuilder(new FakeEmbeddingClient()).BuildAsync();

        var grouped = await _fixture.CreateContext().Set<DocumentChunk>().AsNoTracking()
            .GroupBy(c => c.SourceName)
            .Select(g => new { Source = g.Key, Indexes = g.Select(c => c.ChunkIndex).ToList() })
            .ToListAsync();

        Assert.Equal(DemoCorpus.All.Count, grouped.Count);
        Assert.All(grouped, group =>
            Assert.Equal(Enumerable.Range(0, group.Indexes.Count), group.Indexes.OrderBy(i => i)));
    }

    [Fact]
    public async Task 重複呼叫不會重複灌入()
    {
        var embedding = new FakeEmbeddingClient();
        var first = await CreateBuilder(embedding).BuildAsync();
        var callsAfterFirst = embedding.CallCount;

        var second = await CreateBuilder(embedding).BuildAsync();

        Assert.Equal(first, second);
        Assert.Equal(callsAfterFirst, embedding.CallCount);   // 第二次完全沒有再打 embedding
    }

    [Fact]
    public async Task Ollama不可用時回0並只記警告不拋例外()
    {
        // RAG 是可選模組。它建不起來時整個服務仍然要能啟動，
        // 否則沒裝 Ollama 的人會以為專案壞了
        var logger = new CapturingLogger<RagIndexBuilder>();
        var embedding = new FakeEmbeddingClient
        {
            Failure = new EmbeddingUnavailableException("無法連線到 Ollama（http://localhost:11434）。")
        };

        var count = await CreateBuilder(embedding, logger).BuildAsync();

        Assert.Equal(0, count);
        Assert.Empty(await _fixture.CreateContext().Set<DocumentChunk>().ToListAsync());
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("核心 ERP 與既有工具不受影響", warning.Message);
    }

    [Fact]
    public async Task Ollama中途失敗時不留下半套索引()
    {
        // 半套索引比沒有索引更糟：檢索會回「有答案」，但語料只有前面幾份
        var embedding = new FakeEmbeddingClient();
        var callsBeforeFailure = 0;
        embedding.Override = text =>
        {
            if (++callsBeforeFailure > 5)
            {
                throw new EmbeddingUnavailableException("Ollama 在 10 秒內沒有回應。");
            }
            return [.. Enumerable.Repeat(1f, 64)];
        };

        var count = await CreateBuilder(embedding).BuildAsync();

        Assert.Equal(0, count);
        Assert.Empty(await _fixture.CreateContext().Set<DocumentChunk>().ToListAsync());
    }

    [Fact]
    public async Task 換模型後清掉舊索引重建()
    {
        // 舊向量與新 query 向量不在同一個空間，留著只會算出無意義的分數
        await CreateBuilder(new FakeEmbeddingClient("old-model")).BuildAsync();

        var count = await CreateBuilder(new FakeEmbeddingClient("new-model", dimension: 32)).BuildAsync();

        var chunks = await _fixture.CreateContext().Set<DocumentChunk>().AsNoTracking().ToListAsync();
        Assert.Equal(count, chunks.Count);
        Assert.All(chunks, c => Assert.Equal("new-model", c.EmbeddingModel));
        Assert.All(chunks, c => Assert.Equal(32, c.Dimension));
    }
}
