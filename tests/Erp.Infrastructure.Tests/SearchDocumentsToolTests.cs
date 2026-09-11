using System.Text.Json;
using Erp.Infrastructure.AI;
using Erp.Infrastructure.Persistence;
using Erp.Infrastructure.Rag;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Erp.Infrastructure.Tests;

/// 第九個工具在 tool-use 鏈路上的行為：錯誤契約、稽核軌跡、
/// 以及「Ollama 沒跑不會炸掉整段對話」。
public class SearchDocumentsToolTests : IAsyncLifetime
{
    private static readonly DateOnly Today = new(2026, 9, 10);

    private SqliteTestDatabase _fixture = null!;
    private FakeEmbeddingClient _embedding = null!;
    private TestClock _clock = null!;

    public async Task InitializeAsync()
    {
        _fixture = new SqliteTestDatabase();
        _clock = new TestClock(Today);
        await ErpDbSeeder.SeedAsync(_fixture.Db, _clock);
        _embedding = new FakeEmbeddingClient();
        await TestServices.BuildRagIndexAsync(_fixture, _embedding);
    }

    public async Task DisposeAsync() => await _fixture.DisposeAsync();

    private ToolDispatcher CreateDispatcher(
        IEmbeddingClient? embedding = null, ILogger<ToolDispatcher>? logger = null)
        => TestServices.CreateDispatcher(_fixture, _clock, logger, embedding ?? _embedding);

    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public async Task 成功時回傳snake_case的片段與引用座標()
    {
        var target = await _fixture.CreateContext().Set<DocumentChunk>().AsNoTracking().FirstAsync();

        var result = await CreateDispatcher().ExecuteAsync(
            ToolCatalog.SearchDocuments, Args(new { query = target.Text, top_k = 2 }));

        Assert.False(result.IsError);
        var json = JsonDocument.Parse(result.Content).RootElement;
        var first = json.GetProperty("chunks")[0];
        Assert.Equal(target.SourceName, first.GetProperty("source_name").GetString());
        Assert.Equal(target.ChunkIndex, first.GetProperty("chunk_index").GetInt32());
        Assert.True(first.GetProperty("similarity").GetDouble() > 0.99);
        Assert.True(json.GetProperty("matched_count").GetInt32() >= 1);
        Assert.True(json.TryGetProperty("similarity_threshold", out _));
    }

    [Fact]
    public async Task 回傳的中文不被逃逸成unicode轉義()
    {
        // 逃逸後一個字變六個字元，而工具回傳的片段原文是整個回應裡最長的部分
        var target = await _fixture.CreateContext().Set<DocumentChunk>().AsNoTracking().FirstAsync();

        var result = await CreateDispatcher().ExecuteAsync(
            ToolCatalog.SearchDocuments, Args(new { query = target.Text }));

        Assert.DoesNotContain("\\u", result.Content);
    }

    [Fact]
    public async Task Ollama不可用時回SERVICE_UNAVAILABLE而不是INTERNAL_ERROR()
    {
        // 「這台機器沒裝 Ollama」不是程式故障。歸成 INTERNAL_ERROR 的話，
        // LLM 只能說「查詢失敗」，而這其實是最該給出明確指引的情境
        var broken = new FakeEmbeddingClient
        {
            Failure = new EmbeddingUnavailableException(
                "無法連線到 Ollama（http://localhost:11434）。文件語意檢索需要本機的 Ollama 服務，請確認它已啟動。")
        };

        var result = await CreateDispatcher(broken).ExecuteAsync(
            ToolCatalog.SearchDocuments, Args(new { query = "面板色偏" }));

        Assert.True(result.IsError);
        var json = JsonDocument.Parse(result.Content).RootElement;
        Assert.Equal("SERVICE_UNAVAILABLE", json.GetProperty("error_code").GetString());
        Assert.Contains("Ollama", json.GetProperty("message").GetString()!);
    }

    [Fact]
    public async Task 服務不可用的訊息不含例外型別與堆疊()
    {
        var broken = new FakeEmbeddingClient
        {
            Failure = new EmbeddingUnavailableException(
                "無法連線到 Ollama。", new HttpRequestException("Connection refused"))
        };

        var result = await CreateDispatcher(broken).ExecuteAsync(
            ToolCatalog.SearchDocuments, Args(new { query = "面板色偏" }));

        Assert.DoesNotContain("Exception", result.Content);
        Assert.DoesNotContain("   at ", result.Content);
        Assert.DoesNotContain("Connection refused", result.Content);
    }

    [Fact]
    public async Task 服務不可用時留下警告等級的稽核紀錄()
    {
        var logger = new CapturingLogger<ToolDispatcher>();
        var broken = new FakeEmbeddingClient
        {
            Failure = new EmbeddingUnavailableException("無法連線到 Ollama。")
        };

        await CreateDispatcher(broken, logger).ExecuteAsync(
            ToolCatalog.SearchDocuments, Args(new { query = "面板色偏" }));

        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Warning && e.Message.Contains("search_documents"));
        // 既有那一行通用稽核紀錄照樣要有：工具名稱、成功與否、耗時、參數
        Assert.Contains(logger.Entries, e =>
            e.Message.Contains("工具呼叫 search_documents 完成") && e.Message.Contains("成功：False"));
    }

    [Fact]
    public async Task 成功時的稽核紀錄帶著可讀的中文參數()
    {
        var logger = new CapturingLogger<ToolDispatcher>();

        await CreateDispatcher(logger: logger).ExecuteAsync(
            ToolCatalog.SearchDocuments, Args(new { query = "面板色偏怎麼判定", top_k = 2 }));

        var entry = Assert.Single(logger.Entries, e => e.Message.Contains("工具呼叫 search_documents 完成"));
        Assert.Contains("成功：True", entry.Message);
        Assert.Contains("面板色偏怎麼判定", entry.Message);
    }

    [Fact]
    public async Task 缺少query時回INVALID_ARGUMENT()
    {
        var result = await CreateDispatcher().ExecuteAsync(ToolCatalog.SearchDocuments, Args(new { top_k = 3 }));

        Assert.True(result.IsError);
        Assert.Equal("INVALID_ARGUMENT",
            JsonDocument.Parse(result.Content).RootElement.GetProperty("error_code").GetString());
    }

    [Fact]
    public async Task topK超出上限時回INVALID_ARGUMENT並說明範圍()
    {
        var result = await CreateDispatcher().ExecuteAsync(
            ToolCatalog.SearchDocuments, Args(new { query = "面板色偏", top_k = 99 }));

        Assert.True(result.IsError);
        var json = JsonDocument.Parse(result.Content).RootElement;
        Assert.Equal("INVALID_ARGUMENT", json.GetProperty("error_code").GetString());
        Assert.Contains("1 到 10", json.GetProperty("message").GetString()!);
    }

    [Fact]
    public async Task 查不到相關段落時是成功的空結果而不是錯誤()
    {
        // 查無資料是正常結果，與既有 search_items 的處理一致。
        // 回成錯誤會讓 LLM 以為系統壞了，然後依 error_code 規則去重試
        var orthogonal = new FakeEmbeddingClient { Override = _ => [.. Enumerable.Repeat(-1f, 64)] };

        var result = await CreateDispatcher(orthogonal).ExecuteAsync(
            ToolCatalog.SearchDocuments, Args(new { query = "今天天氣如何" }));

        Assert.False(result.IsError);
        var json = JsonDocument.Parse(result.Content).RootElement;
        Assert.Empty(json.GetProperty("chunks").EnumerateArray());
        Assert.Equal(0, json.GetProperty("matched_count").GetInt32());
        Assert.Contains("沒有找到", json.GetProperty("note").GetString()!);
    }

    [Fact]
    public async Task 低於門檻的片段文字完全不會出現在回傳內容裡()
    {
        // 防幻覺的核心：LLM 看不到低分片段，就不可能「參考一下」。
        // 門檻判斷留在後端，而不是交給無法測試的一方
        var orthogonal = new FakeEmbeddingClient { Override = _ => [.. Enumerable.Repeat(-1f, 64)] };
        var chunks = await _fixture.CreateContext().Set<DocumentChunk>().AsNoTracking().ToListAsync();

        var result = await CreateDispatcher(orthogonal).ExecuteAsync(
            ToolCatalog.SearchDocuments, Args(new { query = "完全無關" }));

        foreach (var chunk in chunks)
        {
            Assert.DoesNotContain(chunk.Text[..20], result.Content);
        }
    }

    [Fact]
    public async Task 索引沒建起來時回SERVICE_UNAVAILABLE而不是空結果()
    {
        await using var withoutIndex = new SqliteTestDatabase();
        await ErpDbSeeder.SeedAsync(withoutIndex.Db, _clock);
        var dispatcher = TestServices.CreateDispatcher(withoutIndex, _clock, embeddingClient: _embedding);

        var result = await dispatcher.ExecuteAsync(
            ToolCatalog.SearchDocuments, Args(new { query = "面板色偏" }));

        Assert.True(result.IsError);
        var json = JsonDocument.Parse(result.Content).RootElement;
        Assert.Equal("SERVICE_UNAVAILABLE", json.GetProperty("error_code").GetString());
        Assert.Contains("索引尚未建立", json.GetProperty("message").GetString()!);
    }

    [Fact]
    public async Task Ollama掛掉時同一輪其他工具的結果仍然送達LLM()
    {
        // 這是整個錯誤處理的價值所在，與既有 G1 的那條測試同一個形狀：
        // 可選模組不可用，不該讓同一輪的庫存查詢一起陣亡
        var broken = new FakeEmbeddingClient
        {
            Failure = new EmbeddingUnavailableException("無法連線到 Ollama。")
        };
        var dispatcher = CreateDispatcher(broken);

        var llm = new FakeLlmClient(
            FakeLlmClient.ToolUses(
                ("t1", ToolCatalog.SearchDocuments, new { query = "面板色偏怎麼處理" }),
                ("t2", ToolCatalog.GetItemInventoryStatus, new { item_code = "PANEL-01" })),
            FakeLlmClient.Text("文件查不到，但面板可用 80 片。"));

        var service = new AiAssistantService(
            llm, dispatcher, Options.Create(new AiAssistantOptions()),
            NullLogger<AiAssistantService>.Instance);

        var answer = await service.AskAsync("色偏怎麼處理？面板還有多少？");

        Assert.Contains("80", answer);
        var results = llm.ReceivedRequests[1].Messages[^1].Content.Cast<LlmToolResultBlock>().ToList();
        Assert.True(results[0].IsError);
        Assert.Contains("SERVICE_UNAVAILABLE", results[0].Content);
        Assert.False(results[1].IsError);
        Assert.Contains("80", results[1].Content);
    }

    [Fact]
    public async Task 工具目錄送給LLM時包含第九個工具且帶選填的topK()
    {
        var llm = new FakeLlmClient(FakeLlmClient.Text("好的"));
        var service = new AiAssistantService(
            llm, CreateDispatcher(), Options.Create(new AiAssistantOptions()),
            NullLogger<AiAssistantService>.Instance);

        await service.AskAsync("色偏怎麼處理？");

        var tool = Assert.Single(
            llm.ReceivedRequests[0].Tools, t => t.Name == ToolCatalog.SearchDocuments);
        Assert.Contains("query", tool.Properties.Keys);
        Assert.Contains("top_k", tool.Properties.Keys);
        Assert.Equal(["query"], tool.Required);
    }
}
