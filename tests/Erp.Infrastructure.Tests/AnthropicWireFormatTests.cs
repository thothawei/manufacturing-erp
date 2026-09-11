using System.Text.Json;
using Erp.Infrastructure.AI;
using Microsoft.Extensions.Options;

namespace Erp.Infrastructure.Tests;

/// 檢查 AnthropicLlmClient 實際送出的 HTTP 請求內容與回應解析。
///
/// 前面的 tool-use 迴圈測試用的是假的 ILlmClient，完全繞過這個類別 ——
/// 型別轉換編譯得過不代表送出去的 JSON 是 API 要的形狀。
/// 這裡把請求導向本機假伺服器，不需要金鑰也不會產生費用。
public class AnthropicWireFormatTests
{
    private static AnthropicLlmClient CreateClient(string baseUrl, int timeoutSeconds = 60) => new(Options.Create(
        new AiAssistantOptions
        {
            ApiKey = "sk-ant-test-key-not-real",
            BaseUrl = baseUrl,
            Model = "claude-opus-5",
            MaxTokens = 8_000,
            TimeoutSeconds = timeoutSeconds
        }));

    /// 打 gateway 的設定：其餘跟 CreateClient 一樣，只差在關掉 refusal fallback
    private static AnthropicLlmClient CreateGatewayClient(string baseUrl) => new(Options.Create(
        new AiAssistantOptions
        {
            ApiKey = "sk-ant-test-key-not-real",
            BaseUrl = baseUrl,
            Model = "claude-opus-5",
            MaxTokens = 8_000,
            UseServerSideFallback = false
        }));

    private static LlmRequest SampleRequest(params LlmMessage[] messages)
        => new("系統提示詞內容", messages, ToolCatalog.All);

    [Fact]
    public async Task 請求包含模型_系統提示詞與完整工具Schema()
    {
        using var server = new FakeAnthropicServer(FakeAnthropicServer.TextResponse("好的"));

        await CreateClient(server.BaseUrl).SendAsync(SampleRequest(LlmMessage.User("面板還有多少？")));

        using var body = JsonDocument.Parse(Assert.Single(server.ReceivedBodies));
        var root = body.RootElement;

        Assert.Equal("claude-opus-5", root.GetProperty("model").GetString());
        Assert.Equal(8_000, root.GetProperty("max_tokens").GetInt32());
        Assert.Contains("系統提示詞內容", root.GetProperty("system").ToString());

        var tools = root.GetProperty("tools");
        Assert.Equal(ToolCatalog.All.Count, tools.GetArrayLength());

        var searchTool = tools.EnumerateArray()
            .Single(t => t.GetProperty("name").GetString() == ToolCatalog.SearchItems);
        var schema = searchTool.GetProperty("input_schema");
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.True(schema.GetProperty("properties").TryGetProperty("keyword", out _));
        Assert.Equal("keyword", schema.GetProperty("required")[0].GetString());
    }

    [Fact]
    public async Task 請求帶上refusal_fallback的beta旗標與備援模型()
    {
        using var server = new FakeAnthropicServer(FakeAnthropicServer.TextResponse("好的"));

        await CreateClient(server.BaseUrl).SendAsync(SampleRequest(LlmMessage.User("測試")));

        Assert.Contains("server-side-fallback", Assert.Single(server.ReceivedBetaHeaders));

        using var body = JsonDocument.Parse(server.ReceivedBodies[0]);
        var fallbacks = body.RootElement.GetProperty("fallbacks");
        Assert.Equal("claude-opus-4-8", fallbacks[0].GetProperty("model").GetString());
    }

    /// OmniRoute 這類相容 gateway 不認得 fallbacks，多送一個它看不懂的欄位
    /// 可能被擋下來；關掉之後要確認是「整個不見」而不是送成 null
    [Fact]
    public async Task 關掉refusal_fallback時beta旗標與參數都不會出現在請求裡()
    {
        using var server = new FakeAnthropicServer(FakeAnthropicServer.TextResponse("好的"));

        await CreateGatewayClient(server.BaseUrl).SendAsync(SampleRequest(LlmMessage.User("測試")));

        Assert.Equal(string.Empty, Assert.Single(server.ReceivedBetaHeaders));

        using var body = JsonDocument.Parse(Assert.Single(server.ReceivedBodies));
        Assert.False(body.RootElement.TryGetProperty("fallbacks", out _));
    }

    [Fact]
    public async Task 請求送到messages端點()
    {
        using var server = new FakeAnthropicServer(FakeAnthropicServer.TextResponse("好的"));

        await CreateClient(server.BaseUrl).SendAsync(SampleRequest(LlmMessage.User("測試")));

        Assert.Contains("/v1/messages", Assert.Single(server.ReceivedPaths));
    }

    [Fact]
    public async Task 工具呼叫與工具結果會序列化成API要的區塊格式()
    {
        using var server = new FakeAnthropicServer(FakeAnthropicServer.TextResponse("完成"));

        // 模擬第二輪：帶著上一輪的 tool_use 與這一輪的 tool_result 再送出
        var toolUse = new LlmToolUseBlock(
            "toolu_01", ToolCatalog.SearchItems,
            JsonSerializer.SerializeToElement(new { keyword = "面板" }));

        await CreateClient(server.BaseUrl).SendAsync(SampleRequest(
            LlmMessage.User("面板"),
            new LlmMessage(LlmRole.Assistant, [toolUse]),
            new LlmMessage(LlmRole.User, [new LlmToolResultBlock("toolu_01", "{\"items\":[]}", IsError: false)])));

        using var body = JsonDocument.Parse(server.ReceivedBodies[0]);
        var messages = body.RootElement.GetProperty("messages");
        Assert.Equal(3, messages.GetArrayLength());

        var assistantBlock = messages[1].GetProperty("content")[0];
        Assert.Equal("tool_use", assistantBlock.GetProperty("type").GetString());
        Assert.Equal("toolu_01", assistantBlock.GetProperty("id").GetString());
        Assert.Equal(ToolCatalog.SearchItems, assistantBlock.GetProperty("name").GetString());
        // input 必須是物件，不是被字串化的 JSON
        Assert.Equal(JsonValueKind.Object, assistantBlock.GetProperty("input").ValueKind);
        Assert.Equal("面板", assistantBlock.GetProperty("input").GetProperty("keyword").GetString());

        var resultBlock = messages[2].GetProperty("content")[0];
        Assert.Equal("tool_result", resultBlock.GetProperty("type").GetString());
        Assert.Equal("toolu_01", resultBlock.GetProperty("tool_use_id").GetString());
        Assert.False(resultBlock.GetProperty("is_error").GetBoolean());
    }

    [Fact]
    public async Task 工具執行失敗會標記is_error()
    {
        using var server = new FakeAnthropicServer(FakeAnthropicServer.TextResponse("完成"));

        await CreateClient(server.BaseUrl).SendAsync(SampleRequest(
            LlmMessage.User("查詢"),
            new LlmMessage(LlmRole.User,
                [new LlmToolResultBlock("toolu_01", "{\"error_code\":\"ENTITY_NOT_FOUND\",\"message\":\"找不到料件\"}", IsError: true)])));

        using var body = JsonDocument.Parse(server.ReceivedBodies[0]);
        var resultBlock = body.RootElement.GetProperty("messages")[1].GetProperty("content")[0];
        Assert.True(resultBlock.GetProperty("is_error").GetBoolean());
    }

    [Fact]
    public async Task 回應的工具呼叫區塊會被解析成中性模型()
    {
        using var server = new FakeAnthropicServer(
            FakeAnthropicServer.ToolUseResponse("toolu_99", ToolCatalog.GetItemInventoryStatus,
                "{\"item_code\":\"PANEL-01\"}"));

        var response = await CreateClient(server.BaseUrl)
            .SendAsync(SampleRequest(LlmMessage.User("庫存")));

        Assert.True(response.RequiresToolExecution);
        var toolUse = Assert.Single(response.ToolUses);
        Assert.Equal("toolu_99", toolUse.ToolUseId);
        Assert.Equal(ToolCatalog.GetItemInventoryStatus, toolUse.ToolName);
        Assert.Equal("PANEL-01", toolUse.Arguments.GetProperty("item_code").GetString());
    }

    [Fact]
    public async Task 回應的純文字會被解析成答案()
    {
        using var server = new FakeAnthropicServer(
            FakeAnthropicServer.TextResponse("面板目前可用庫存 80 片。"));

        var response = await CreateClient(server.BaseUrl)
            .SendAsync(SampleRequest(LlmMessage.User("庫存")));

        Assert.False(response.RequiresToolExecution);
        Assert.Equal("面板目前可用庫存 80 片。", response.Text);
    }

    [Fact]
    public async Task 中文不會被逃逸成unicode跳脫序列()
    {
        using var server = new FakeAnthropicServer(FakeAnthropicServer.TextResponse("完成"));

        await CreateClient(server.BaseUrl).SendAsync(SampleRequest(LlmMessage.User("面板還有多少可以用？")));

        var body = server.ReceivedBodies[0];

        // SDK 預設會逃逸中文，實測讓請求體積變成 2.28 倍，且工具說明每輪都重送。
        // UnicodeNormalizingHandler 負責在送出前還原，這條測試守住它。
        Assert.Contains("面板還有多少可以用", body);
        Assert.DoesNotContain("\\u9762", body);
        Assert.Contains("以關鍵字搜尋料件主檔", body);   // 工具說明同樣不該被逃逸
    }

    [Fact]
    public async Task 逾時會轉成可讀的錯誤而不是原始的取消例外()
    {
        using var server = new FakeAnthropicServer(FakeAnthropicServer.TextResponse("太慢了"))
        {
            ResponseDelay = TimeSpan.FromSeconds(5)
        };

        var ex = await Assert.ThrowsAsync<LlmUnavailableException>(() =>
            CreateClient(server.BaseUrl, timeoutSeconds: 1)
                .SendAsync(SampleRequest(LlmMessage.User("測試"))));

        Assert.Contains("1 秒內沒有回應", ex.Message);
    }
}
