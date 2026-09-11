using System.Text.Json;
using Erp.Infrastructure.AI;
using Erp.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Erp.Infrastructure.Tests;

/// tool-use 迴圈的測試。用假的 ILlmClient，不需要 API key，也不會產生費用。
public class AiAssistantServiceTests : IAsyncLifetime
{
    private static readonly DateOnly Today = new(2026, 9, 10);

    private SqliteTestDatabase _fixture = null!;
    private ToolDispatcher _dispatcher = null!;

    public async Task InitializeAsync()
    {
        _fixture = new SqliteTestDatabase();
        var clock = new TestClock(Today);
        await ErpDbSeeder.SeedAsync(_fixture.Db, clock);

        _dispatcher = TestServices.CreateDispatcher(_fixture, clock);
    }

    public async Task DisposeAsync() => await _fixture.DisposeAsync();

    private AiAssistantService CreateService(FakeLlmClient llm, int maxIterations = 5)
        => new(llm, _dispatcher,
            Options.Create(new AiAssistantOptions { MaxToolIterations = maxIterations }),
            NullLogger<AiAssistantService>.Instance);

    [Fact]
    public async Task 工具呼叫的結果會回送給LLM並產出最終答案()
    {
        var llm = new FakeLlmClient(
            FakeLlmClient.ToolUse("tu_1", ToolCatalog.GetItemInventoryStatus, new { item_code = "PANEL-01" }),
            FakeLlmClient.Text("面板目前可用庫存 80 片。"));

        var answer = await CreateService(llm).AskAsync("面板還有多少可以用？");

        Assert.Equal("面板目前可用庫存 80 片。", answer);
        Assert.Equal(2, llm.ReceivedRequests.Count);

        // 第二次請求要包含：原問題、assistant 的 tool_use、以及對應的 tool_result
        var secondRequest = llm.ReceivedRequests[1];
        Assert.Equal(3, secondRequest.Messages.Count);

        var toolResult = Assert.IsType<LlmToolResultBlock>(
            Assert.Single(secondRequest.Messages[2].Content));
        Assert.Equal("tu_1", toolResult.ToolUseId);
        Assert.False(toolResult.IsError);
        Assert.Contains("\"available_qty\":80", toolResult.Content);
    }

    /// 空字串不是答案。會走到這裡的是「整個回應只有 thinking 區塊」，
    /// 或 gateway 的空內容佔位符被 LLM client 丟掉之後什麼都不剩
    [Fact]
    public async Task LLM沒有回覆內容時給出說得清楚的訊息而不是空字串()
    {
        var answer = await CreateService(new FakeLlmClient(new LlmResponse([]))).AskAsync("面板還有多少？");

        Assert.Equal("AI 這次沒有回覆任何內容，請換個問法再試一次。", answer);
    }

    [Fact]
    public async Task 工具回傳的JSON使用snake_case欄位名()
    {
        var llm = new FakeLlmClient(
            FakeLlmClient.ToolUse("tu_1", ToolCatalog.GetItemInventoryStatus, new { item_code = "PANEL-01" }),
            FakeLlmClient.Text("完成"));

        await CreateService(llm).AskAsync("查庫存");

        var content = ((LlmToolResultBlock)llm.ReceivedRequests[1].Messages[2].Content[0]).Content;
        using var json = JsonDocument.Parse(content);

        // 欄位名要跟工具說明裡承諾的一致，否則 LLM 會找不到欄位
        Assert.True(json.RootElement.TryGetProperty("on_hand_qty", out _));
        Assert.True(json.RootElement.TryGetProperty("reserved_qty", out _));
        Assert.True(json.RootElement.TryGetProperty("available_qty", out _));
        Assert.True(json.RootElement.TryGetProperty("item_name", out _));
    }

    [Fact]
    public async Task 同一輪的多個工具呼叫_結果全部放在同一則訊息裡()
    {
        // 拆成多則訊息會讓 API 拒絕，也會讓 LLM 後續不再平行呼叫工具
        var llm = new FakeLlmClient(
            FakeLlmClient.ToolUses(
                ("tu_1", ToolCatalog.SearchItems, new { keyword = "面板" }),
                ("tu_2", ToolCatalog.GetItemInventoryStatus, new { item_code = "SCREW-05" })),
            FakeLlmClient.Text("查詢完成"));

        await CreateService(llm).AskAsync("面板和螺絲的狀況？");

        var followUp = llm.ReceivedRequests[1];
        Assert.Equal(3, followUp.Messages.Count);

        var resultMessage = followUp.Messages[2];
        Assert.Equal(LlmRole.User, resultMessage.Role);
        Assert.Equal(2, resultMessage.Content.Count);
        Assert.Equal(["tu_1", "tu_2"],
            resultMessage.Content.Cast<LlmToolResultBlock>().Select(r => r.ToolUseId));
    }

    [Fact]
    public async Task 查無料件時如實回報錯誤而不是中斷()
    {
        var llm = new FakeLlmClient(
            FakeLlmClient.ToolUse("tu_1", ToolCatalog.GetItemInventoryStatus, new { item_code = "NOT-EXIST" }),
            FakeLlmClient.Text("查無此料號。"));

        var answer = await CreateService(llm).AskAsync("NOT-EXIST 還有多少？");

        Assert.Equal("查無此料號。", answer);

        var toolResult = (LlmToolResultBlock)llm.ReceivedRequests[1].Messages[2].Content[0];
        Assert.True(toolResult.IsError);
        Assert.Contains("ENTITY_NOT_FOUND", toolResult.Content);
        Assert.Contains("找不到料件", toolResult.Content);
    }

    [Fact]
    public async Task 未知的工具名稱回報錯誤而不是擲出例外()
    {
        var llm = new FakeLlmClient(
            FakeLlmClient.ToolUse("tu_1", "delete_all_work_orders", new { }),
            FakeLlmClient.Text("我只能查詢，無法異動資料。"));

        var answer = await CreateService(llm).AskAsync("把工單全部刪掉");

        Assert.Equal("我只能查詢，無法異動資料。", answer);

        var toolResult = (LlmToolResultBlock)llm.ReceivedRequests[1].Messages[2].Content[0];
        Assert.True(toolResult.IsError);
        Assert.Contains("UNKNOWN_TOOL", toolResult.Content);
    }

    [Fact]
    public async Task 缺少必要參數時回報參數錯誤()
    {
        var llm = new FakeLlmClient(
            FakeLlmClient.ToolUse("tu_1", ToolCatalog.GetItemInventoryStatus, new { wrong_name = "PANEL-01" }),
            FakeLlmClient.Text("需要料號才能查詢。"));

        await CreateService(llm).AskAsync("查庫存");

        var toolResult = (LlmToolResultBlock)llm.ReceivedRequests[1].Messages[2].Content[0];
        Assert.True(toolResult.IsError);
        Assert.Contains("INVALID_ARGUMENT", toolResult.Content);
        Assert.Contains("缺少必要參數 item_code", toolResult.Content);
    }

    [Fact]
    public async Task 迴圈達到上限時停止並回覆說明_不會無限呼叫下去()
    {
        var llm = new FakeLlmClient
        {
            // LLM 每一輪都要求再呼叫一次工具，永遠不給文字答案
            RepeatingResponse = FakeLlmClient.ToolUse(
                "tu_x", ToolCatalog.SearchItems, new { keyword = "面板" })
        };

        var answer = await CreateService(llm, maxIterations: 3).AskAsync("繞圈圈");

        Assert.Equal(3, llm.ReceivedRequests.Count);
        Assert.Contains("超過上限", answer);
    }

    [Fact]
    public async Task 不需要工具的問題直接回答_不呼叫任何工具()
    {
        var llm = new FakeLlmClient(FakeLlmClient.Text("我可以幫你查料件、庫存等資訊。"));

        var answer = await CreateService(llm).AskAsync("你可以做什麼？");

        Assert.Equal("我可以幫你查料件、庫存等資訊。", answer);
        Assert.Single(llm.ReceivedRequests);
    }

    [Fact]
    public async Task 每次請求都帶上系統提示詞與完整工具清單()
    {
        var llm = new FakeLlmClient(
            FakeLlmClient.ToolUse("tu_1", ToolCatalog.SearchItems, new { keyword = "面板" }),
            FakeLlmClient.Text("完成"));

        await CreateService(llm).AskAsync("面板");

        Assert.All(llm.ReceivedRequests, request =>
        {
            Assert.Equal(AiSystemPrompt.Text, request.SystemPrompt);
            Assert.Equal(ToolCatalog.All.Count, request.Tools.Count);
        });
    }

    [Fact]
    public async Task 空白問題會被擋下()
    {
        var llm = new FakeLlmClient();

        await Assert.ThrowsAsync<ArgumentException>(() => CreateService(llm).AskAsync("   "));
        Assert.Empty(llm.ReceivedRequests);
    }
}
