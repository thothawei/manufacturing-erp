using System.Text.Json;
using Erp.Infrastructure.AI;
using Erp.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Erp.Infrastructure.Tests;

/// 走完整條鏈：假 LLM 要求呼叫工具 → 真的 EF Core 查詢 → 真的種子資料 →
/// 結果序列化回送。重點不是 LLM 說了什麼，而是「送進 LLM 的數字是後端算出來的」。
///
/// 這裡釘住的情境就是 docs/ai-assistant-module-plan-v2.md 第 5 節的兩個範例。
public class AiAssistantScenarioTests : IAsyncLifetime
{
    private static readonly DateOnly Today = new(2026, 9, 10);
    private static readonly string Stamp = Today.ToString("yyyyMMdd");

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

    private AiAssistantService CreateService(FakeLlmClient llm)
        => new(llm, _dispatcher,
            Options.Create(new AiAssistantOptions()),
            NullLogger<AiAssistantService>.Instance);

    /// 從第 N 次請求裡取出第一個工具結果的 JSON
    private static JsonElement ToolResultOf(FakeLlmClient llm, int requestIndex, int blockIndex = 0)
    {
        var message = llm.ReceivedRequests[requestIndex].Messages[^1];
        var block = (LlmToolResultBlock)message.Content[blockIndex];
        Assert.False(block.IsError, $"工具執行失敗：{block.Content}");
        return JsonDocument.Parse(block.Content).RootElement.Clone();
    }

    [Fact]
    public async Task 範例一_詢問最多能做幾台_LLM收到的是後端算出的可製造量()
    {
        var llm = new FakeLlmClient(
            FakeLlmClient.ToolUse("t1", ToolCatalog.SearchItems, new { keyword = "TV-100" }),
            FakeLlmClient.ToolUse("t2", ToolCatalog.CheckMaterialSufficiency, new { item_code = "TV-100" }),
            FakeLlmClient.Text("以目前可用庫存，TV-100 最多可以生產 40 台。"));

        var answer = await CreateService(llm).AskAsync("TV-100 用現有庫存最多可以做幾台？");

        var search = ToolResultOf(llm, 1);
        Assert.Equal("TV-100", search[0].GetProperty("item_code").GetString());
        Assert.Equal("FinishedGood", search[0].GetProperty("item_type").GetString());

        var sufficiency = ToolResultOf(llm, 2);
        Assert.Equal(40, sufficiency.GetProperty("max_buildable_qty").GetInt32());
        Assert.Equal("available", sufficiency.GetProperty("basis").GetString());
        Assert.Equal("v3", sufficiency.GetProperty("bom_version").GetString());

        Assert.Contains("40 台", answer);
    }

    [Fact]
    public async Task 範例二_風險工單與採購建議_數字全部來自工具回傳()
    {
        var llm = new FakeLlmClient(
            FakeLlmClient.ToolUse("t1", ToolCatalog.ListWorkOrdersAtRisk, new { }),
            FakeLlmClient.ToolUse("t2", ToolCatalog.RunMrpShortageAnalysis, new { }),
            FakeLlmClient.Text("本週風險工單兩張，建議向 SUP-008 採購面板 150 片。"));

        await CreateService(llm).AskAsync("這週有哪些工單有延遲風險？缺料的話幫我列建議採購清單。");

        var risks = ToolResultOf(llm, 1);
        var shortageWo = risks.EnumerateArray()
            .Single(r => r.GetProperty("work_order_no").GetString() == $"WO-{Stamp}-01");
        Assert.Equal(2, shortageWo.GetProperty("delay_days").GetInt32());
        Assert.Contains("PANEL-01 短少 120 件", shortageWo.GetProperty("risk_reason").GetString());

        var mrp = ToolResultOf(llm, 2);
        var panel = mrp.GetProperty("shortage_items").EnumerateArray()
            .Single(s => s.GetProperty("item_code").GetString() == "PANEL-01");
        Assert.Equal(130, panel.GetProperty("net_shortage_qty").GetInt32());
        Assert.Equal(150, panel.GetProperty("suggested_order_qty").GetInt32());
        Assert.Equal("SUP-008", panel.GetProperty("supplier_code").GetString());
        Assert.Equal(5, panel.GetProperty("lead_time_days").GetInt32());
    }

    [Fact]
    public async Task 工單進度查詢_途程站別依序回傳且含發料狀態()
    {
        var llm = new FakeLlmClient(
            FakeLlmClient.ToolUse("t1", ToolCatalog.GetWorkOrderProgress, new { work_order_no = $"WO-{Stamp}-02" }),
            FakeLlmClient.Text("已完成 20 台。"));

        await CreateService(llm).AskAsync($"WO-{Stamp}-02 做到哪了？");

        var progress = ToolResultOf(llm, 1);
        Assert.Equal("MON-200", progress.GetProperty("item_code").GetString());
        Assert.Equal("InProgress", progress.GetProperty("status").GetString());
        Assert.Equal("已全數發料", progress.GetProperty("material_issue_status").GetString());

        var steps = progress.GetProperty("routing_steps");
        Assert.Equal(2, steps.GetArrayLength());
        Assert.Equal(10, steps[0].GetProperty("step_no").GetInt32());
        Assert.Equal(20, steps[1].GetProperty("completed_qty").GetInt32());
    }

    [Fact]
    public async Task 採購單查詢_只回未結案的且含在途資訊()
    {
        var llm = new FakeLlmClient(
            FakeLlmClient.ToolUse("t1", ToolCatalog.ListOpenPurchaseOrders, new { item_code = "PANEL-01" }),
            FakeLlmClient.Text("面板有一張採購單在途。"));

        await CreateService(llm).AskAsync("面板的採購單狀況？");

        var orders = ToolResultOf(llm, 1);
        var order = Assert.Single(orders.EnumerateArray().ToList());
        Assert.Equal("Open", order.GetProperty("status").GetString());
        Assert.Equal(30, order.GetProperty("ordered_qty").GetInt32());
        Assert.Equal(0, order.GetProperty("received_qty").GetInt32());
    }

    [Fact]
    public async Task 品管查詢_多次檢驗已由後端彙總_LLM不需自己加總()
    {
        var llm = new FakeLlmClient(
            FakeLlmClient.ToolUse("t1", ToolCatalog.GetQualityInspectionSummary,
                new { work_order_no = $"WO-{Stamp}-02" }),
            FakeLlmClient.Text("檢驗 20 台，不合格 3 台。"));

        await CreateService(llm).AskAsync($"WO-{Stamp}-02 的品管結果？");

        var summaries = ToolResultOf(llm, 1);
        var summary = Assert.Single(summaries.EnumerateArray().ToList());
        Assert.Equal(20, summary.GetProperty("inspected_qty").GetInt32());
        Assert.Equal(17, summary.GetProperty("passed_qty").GetInt32());
        Assert.Equal(3, summary.GetProperty("failed_qty").GetInt32());
        Assert.Equal("外觀刮傷、亮點超標", summary.GetProperty("fail_reason_summary").GetString());
    }

    [Fact]
    public async Task 對原物料問可製造量會得到明確錯誤而不是零()
    {
        // PANEL-01 是原物料沒有 BOM。回傳 0 會讓 LLM 說「一台都做不出來」，
        // 那是錯的答案；必須讓它知道這個問法不適用。
        var llm = new FakeLlmClient(
            FakeLlmClient.ToolUse("t1", ToolCatalog.CheckMaterialSufficiency, new { item_code = "PANEL-01" }),
            FakeLlmClient.Text("PANEL-01 是原物料，沒有 BOM。"));

        await CreateService(llm).AskAsync("PANEL-01 最多能做幾個？");

        var block = (LlmToolResultBlock)llm.ReceivedRequests[1].Messages[^1].Content[0];
        Assert.True(block.IsError);
        Assert.Contains("NOT_APPLICABLE", block.Content);
        Assert.Contains("沒有 BOM", block.Content);
    }

    [Fact]
    public async Task 指定產量時會回報夠不夠做()
    {
        var llm = new FakeLlmClient(
            FakeLlmClient.ToolUse("t1", ToolCatalog.CheckMaterialSufficiency,
                new { item_code = "TV-100", planned_qty = 100 }),
            FakeLlmClient.Text("不夠，面板短少 120 片。"));

        await CreateService(llm).AskAsync("TV-100 要做 100 台夠不夠？");

        var result = ToolResultOf(llm, 1);
        Assert.False(result.GetProperty("sufficient_for_requested_qty").GetBoolean());

        var shortage = result.GetProperty("shortage_components").EnumerateArray()
            .Single(c => c.GetProperty("component_code").GetString() == "PANEL-01");
        Assert.Equal(120, shortage.GetProperty("shortfall_qty").GetInt32());
        Assert.Equal(2, shortage.GetProperty("required_per_finished_unit").GetInt32());
    }

    [Fact]
    public async Task 數字以字串傳入時仍能正確解析()
    {
        // LLM 偶爾會把數字包成字串送出，這種情況不該變成參數錯誤
        var llm = new FakeLlmClient(
            FakeLlmClient.ToolUse("t1", ToolCatalog.CheckMaterialSufficiency,
                new { item_code = "TV-100", planned_qty = "100" }),
            FakeLlmClient.Text("不夠。"));

        await CreateService(llm).AskAsync("TV-100 做 100 台夠嗎？");

        var result = ToolResultOf(llm, 1);
        Assert.False(result.GetProperty("sufficient_for_requested_qty").GetBoolean());
    }

    [Fact]
    public async Task 日期格式錯誤時回報明確訊息()
    {
        var llm = new FakeLlmClient(
            FakeLlmClient.ToolUse("t1", ToolCatalog.ListWorkOrdersAtRisk,
                new { date_range_start = "上週一" }),
            FakeLlmClient.Text("日期格式不正確。"));

        await CreateService(llm).AskAsync("上週的風險工單");

        var block = (LlmToolResultBlock)llm.ReceivedRequests[1].Messages[^1].Content[0];
        Assert.True(block.IsError);
        Assert.Contains("INVALID_ARGUMENT", block.Content);
        Assert.Contains("YYYY-MM-DD", block.Content);
    }
}
