using Erp.Infrastructure.AI;
using Erp.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Erp.Infrastructure.Tests;

/// 離線驗證 AgentEvaluationHarness 的計分邏輯本身有沒有算對 —— 這組測試完全不碰真實 API，
/// 用 FakeLlmClient 或直接建構 CaseResult。
///
/// 跟 AgentEvaluationTests（live，需金鑰）的分工跟 PromptInjectionResilienceTests 是同一個道理：
/// 「真模型會不會答對」驗不了（那要接真 LLM），但「答錯的時候計分器抓不抓得到」驗得了，
/// 而且要驗得到 —— 計分邏輯本身沒有測試釘住的話，S4 全部的數字都不可信。
public class AgentEvaluationHarnessTests : IAsyncLifetime
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

    private AiAssistantService CreateService(FakeLlmClient llm, out RecordingLlmClient recorder)
    {
        recorder = new RecordingLlmClient(llm);
        return new AiAssistantService(
            recorder, _dispatcher, TestServices.CreateConversationStore(),
            Options.Create(new AiAssistantOptions()), NullLogger<AiAssistantService>.Instance);
    }

    [Fact]
    public async Task 正確案例_工具對答案也只用工具回傳過的數字_全部判定為對()
    {
        var evalCase = new AgentEvalCase(
            "TV-100 用現有庫存最多可以做幾台？", AgentEvalCaseKind.SingleTool, ["check_material_sufficiency_for_item"]);

        var llm = new FakeLlmClient(
            FakeLlmClient.ToolUse("t1", ToolCatalog.CheckMaterialSufficiency, new { item_code = "TV-100" }),
            FakeLlmClient.Text("以目前可用庫存，TV-100 最多可以生產 40 台。"));

        var service = CreateService(llm, out var recorder);
        var result = await AgentEvaluationHarness.RunAsync(service, recorder, evalCase, Stamp);

        Assert.True(result.ToolSelectionOk);
        Assert.True(result.RefusalOk);
        Assert.Empty(result.HallucinatedNumbers);
    }

    [Fact]
    public async Task 答案裡混進工具從沒回傳過的數字_會被標成幻覺()
    {
        var evalCase = new AgentEvalCase(
            "TV-100 用現有庫存最多可以做幾台？", AgentEvalCaseKind.SingleTool, ["check_material_sufficiency_for_item"]);

        var llm = new FakeLlmClient(
            FakeLlmClient.ToolUse("t1", ToolCatalog.CheckMaterialSufficiency, new { item_code = "TV-100" }),
            // 工具真正回傳的是 40，這裡編了一個從沒出現過的 999
            FakeLlmClient.Text("以目前可用庫存，TV-100 最多可以生產 999 台。"));

        var service = CreateService(llm, out var recorder);
        var result = await AgentEvaluationHarness.RunAsync(service, recorder, evalCase, Stamp);

        // 反向對照：上一條測試證明「只用工具回傳過的數字」判定為乾淨，
        // 這裡只換掉答案文字，其餘完全一樣 —— 偵測器必須因此改變判定，不然它形同沒接上
        var hallucinated = Assert.Single(result.HallucinatedNumbers);
        Assert.Equal("999", hallucinated);
    }

    [Fact]
    public async Task 工具選錯時_工具選擇正確率判定為錯()
    {
        var evalCase = new AgentEvalCase(
            "TV-100 用現有庫存最多可以做幾台？", AgentEvalCaseKind.SingleTool, ["check_material_sufficiency_for_item"]);

        // 模型答非所問，查的是庫存而不是可製造量
        var llm = new FakeLlmClient(
            FakeLlmClient.ToolUse("t1", ToolCatalog.GetItemInventoryStatus, new { item_code = "TV-100" }),
            FakeLlmClient.Text("TV-100 現有庫存 12 台。"));

        var service = CreateService(llm, out var recorder);
        var result = await AgentEvaluationHarness.RunAsync(service, recorder, evalCase, Stamp);

        Assert.False(result.ToolSelectionOk);
    }

    [Fact]
    public async Task 查無資料的案例_答案有查無字樣時判定拒答正確()
    {
        var evalCase = new AgentEvalCase(
            "TV-999 這個料號現在庫存多少？", AgentEvalCaseKind.Refusal,
            ["get_item_inventory_status"], ExpectRefusal: true);

        var llm = new FakeLlmClient(
            FakeLlmClient.ToolUse("t1", ToolCatalog.GetItemInventoryStatus, new { item_code = "TV-999" }),
            FakeLlmClient.Text("查無 TV-999 這個料號，請確認料號是否正確。"));

        var service = CreateService(llm, out var recorder);
        var result = await AgentEvaluationHarness.RunAsync(service, recorder, evalCase, Stamp);

        Assert.True(result.RefusalOk);
    }

    [Fact]
    public async Task 查無資料的案例_模型硬編一個答案時判定拒答錯誤()
    {
        var evalCase = new AgentEvalCase(
            "TV-999 這個料號現在庫存多少？", AgentEvalCaseKind.Refusal,
            ["get_item_inventory_status"], ExpectRefusal: true);

        // 工具其實回了 EntityNotFound 錯誤，但劇本讓模型硬答一個數字 —— 這正是 S4 要抓的失效模式
        var llm = new FakeLlmClient(
            FakeLlmClient.ToolUse("t1", ToolCatalog.GetItemInventoryStatus, new { item_code = "TV-999" }),
            FakeLlmClient.Text("TV-999 目前可用庫存還有 30 台。"));

        var service = CreateService(llm, out var recorder);
        var result = await AgentEvaluationHarness.RunAsync(service, recorder, evalCase, Stamp);

        // 拒答判定要錯，而且那個編出來的 30 也要被抓到 —— 兩條防線都要接上
        Assert.False(result.RefusalOk);
        Assert.Contains("30", result.HallucinatedNumbers);
    }

    [Fact]
    public void Aggregate_正確彙總工具選擇率與幻覺率與拒答率()
    {
        var okCase = new AgentEvalCase("q1", AgentEvalCaseKind.SingleTool, ["t"]);
        var badToolCase = new AgentEvalCase("q2", AgentEvalCaseKind.SingleTool, ["t"]);
        var refusalCase = new AgentEvalCase("q3", AgentEvalCaseKind.Refusal, ["t"], ExpectRefusal: true);

        var results = new[]
        {
            new AgentEvaluationHarness.CaseResult(okCase, "q1", "40", ["t"], true, true, [], 100, 50, 10),
            new AgentEvaluationHarness.CaseResult(badToolCase, "q2", "40", ["other"], false, true, ["999"], 100, 50, 20),
            new AgentEvaluationHarness.CaseResult(refusalCase, "q3", "查無", ["t"], true, false, [], 100, 50, 30),
        };

        var metrics = AgentEvaluationHarness.Aggregate(results);

        Assert.Equal(3, metrics.TotalCases);
        Assert.Equal(2.0 / 3, metrics.ToolSelectionAccuracy, precision: 10);
        Assert.Equal(1.0 / 3, metrics.NumericHallucinationRate, precision: 10);
        Assert.Equal(0.0, metrics.RefusalAccuracy, precision: 10);
        Assert.Equal(100, metrics.AvgInputTokens, precision: 10);
        Assert.Equal(50, metrics.AvgOutputTokens, precision: 10);
    }

    [Fact]
    public void EstimateCostUsd_已知模型算得出金額_未知模型回傳null()
    {
        var cost = AgentEvaluationHarness.EstimateCostUsd("claude-opus-5", 1_000_000, 1_000_000);
        Assert.Equal(30m, cost); // 5 (input) + 25 (output)

        var viaGateway = AgentEvaluationHarness.EstimateCostUsd("anthropic/claude-opus-5", 1_000_000, 0);
        Assert.Equal(5m, viaGateway); // 前綴要能正確剝掉

        Assert.Null(AgentEvaluationHarness.EstimateCostUsd("some-unknown-model", 1_000, 1_000));
    }

    [Fact]
    public void BuildReport_一鍵產生的報表含整體指標與失敗案例區塊()
    {
        var failCase = new AgentEvalCase("q1", AgentEvalCaseKind.SingleTool, ["t"]);
        var results = new[]
        {
            new AgentEvaluationHarness.CaseResult(
                failCase, "q1", "答案", ["other"], false, true, ["999"], 100, 50, 10),
        };
        var metrics = AgentEvaluationHarness.Aggregate(results, "claude-opus-5");

        var report = AgentEvaluationHarness.BuildReport(results, metrics, "claude-opus-5");

        Assert.Contains("## 整體指標", report);
        Assert.Contains("## 依類別分解", report);
        Assert.Contains("## 失敗案例", report);
        Assert.Contains("999", report);
        Assert.Contains("US$", report);
    }
}
