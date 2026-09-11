using Erp.Infrastructure.Rag;
using Microsoft.Extensions.Options;

namespace Erp.Infrastructure.Tests;

/// OllamaEmbeddingClient 的 wire format 與錯誤契約。
///
/// 「本機沒裝 Ollama」是這個模組最常見的情境，它必須變成一個有指引的錯誤訊息，
/// 而不是一個未預期例外穿過 tool-use 迴圈把整段對話炸成 500。
public class OllamaEmbeddingClientTests
{
    private static OllamaEmbeddingClient CreateClient(
        string baseUrl, string model = "nomic-embed-text", int timeoutSeconds = 10)
        => new(Options.Create(new RagOptions
        {
            OllamaBaseUrl = baseUrl,
            EmbeddingModel = model,
            TimeoutSeconds = timeoutSeconds
        }));

    [Fact]
    public async Task 送出的請求符合Ollama的embeddings介面()
    {
        using var server = new FakeOllamaServer();
        using var client = CreateClient(server.BaseUrl, "nomic-embed-text");

        var vector = await client.EmbedAsync("面板色偏怎麼判定");

        Assert.Equal("POST", server.ReceivedMethods[0]);
        Assert.Equal("/api/embeddings", server.ReceivedPaths[0]);
        Assert.Contains("\"model\":\"nomic-embed-text\"", server.ReceivedBodies[0]);
        Assert.Contains("\"prompt\":", server.ReceivedBodies[0]);
        Assert.Equal([0.1f, 0.2f, 0.3f, 0.4f], vector);
    }

    [Fact]
    public async Task 請求體不把中文逃逸成unicode轉義()
    {
        // 逃逸後的請求體是合法 JSON，但抓包或看 Ollama log 時讀不出來送了什麼，
        // 與既有的 UnicodeNormalizingHandler 同一個理由
        using var server = new FakeOllamaServer();
        using var client = CreateClient(server.BaseUrl);

        await client.EmbedAsync("面板色偏");

        Assert.Contains("面板色偏", server.ReceivedBodies[0]);
        Assert.DoesNotContain("\\u", server.ReceivedBodies[0]);
    }

    [Fact]
    public async Task 沒裝或沒啟動Ollama時回可行動的訊息()
    {
        using var client = CreateClient(FakeOllamaServer.UnusedBaseUrl());

        var ex = await Assert.ThrowsAsync<EmbeddingUnavailableException>(
            () => client.EmbedAsync("面板色偏"));

        Assert.Contains("無法連線到 Ollama", ex.Message);
        Assert.Contains("已啟動", ex.Message);
    }

    [Fact]
    public async Task 模型沒pull時訊息要寫明解法()
    {
        // 「HTTP 404」對使用者沒有任何行動指引
        using var server = new FakeOllamaServer { StatusCode = 404, ResponseBody = "{\"error\":\"model not found\"}" };
        using var client = CreateClient(server.BaseUrl, "mxbai-embed-large");

        var ex = await Assert.ThrowsAsync<EmbeddingUnavailableException>(
            () => client.EmbedAsync("面板色偏"));

        Assert.Contains("ollama pull mxbai-embed-large", ex.Message);
    }

    [Fact]
    public async Task 伺服器錯誤轉成服務不可用()
    {
        using var server = new FakeOllamaServer { StatusCode = 500 };
        using var client = CreateClient(server.BaseUrl);

        var ex = await Assert.ThrowsAsync<EmbeddingUnavailableException>(
            () => client.EmbedAsync("面板色偏"));

        Assert.Contains("500", ex.Message);
    }

    [Fact]
    public async Task 回應沒有向量欄位時報錯而不是回空向量()
    {
        // 硬往下走會把空向量寫進索引，之後每次查詢都算出 0 分 ——
        // 靜默壞掉比直接報錯難追得多
        using var server = new FakeOllamaServer { ResponseBody = "{\"model\":\"x\"}" };
        using var client = CreateClient(server.BaseUrl);

        var ex = await Assert.ThrowsAsync<EmbeddingUnavailableException>(
            () => client.EmbedAsync("面板色偏"));

        Assert.Contains("沒有向量資料", ex.Message);
    }

    [Fact]
    public async Task 向量是空陣列時也報錯()
    {
        using var server = new FakeOllamaServer { ResponseBody = "{\"embedding\":[]}" };
        using var client = CreateClient(server.BaseUrl);

        await Assert.ThrowsAsync<EmbeddingUnavailableException>(() => client.EmbedAsync("面板色偏"));
    }

    [Fact]
    public async Task 回應不是有效JSON時轉成服務不可用()
    {
        using var server = new FakeOllamaServer { ResponseBody = "not json at all" };
        using var client = CreateClient(server.BaseUrl);

        await Assert.ThrowsAsync<EmbeddingUnavailableException>(() => client.EmbedAsync("面板色偏"));
    }

    [Fact]
    public async Task 逾時轉成服務不可用並寫出秒數()
    {
        using var server = new FakeOllamaServer { ResponseDelay = TimeSpan.FromSeconds(3) };
        using var client = CreateClient(server.BaseUrl, timeoutSeconds: 1);

        var ex = await Assert.ThrowsAsync<EmbeddingUnavailableException>(
            () => client.EmbedAsync("面板色偏"));

        Assert.Contains("1 秒內沒有回應", ex.Message);
    }

    [Fact]
    public async Task 呼叫端主動取消時不偽裝成服務不可用()
    {
        // 使用者關掉連線不是 Ollama 的問題，原樣往上拋讓迴圈停下來
        using var server = new FakeOllamaServer { ResponseDelay = TimeSpan.FromSeconds(3) };
        using var client = CreateClient(server.BaseUrl, timeoutSeconds: 30);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.EmbedAsync("面板色偏", cts.Token));
    }

    [Fact]
    public async Task 空字串不送出請求()
    {
        using var server = new FakeOllamaServer();
        using var client = CreateClient(server.BaseUrl);

        await Assert.ThrowsAsync<ArgumentException>(() => client.EmbedAsync("   "));
        Assert.Empty(server.ReceivedBodies);
    }
}
