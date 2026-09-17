using System.Diagnostics;
using Erp.Infrastructure.AI;
using Erp.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Erp.Infrastructure.Tests;

/// S4：Agent 端到端評測，接真實 Claude 跑完整份 AgentEvaluationSet。
/// 沒有金鑰時整組 skip，機制跟 AnthropicLiveApiTests 完全一樣（同一個 AnthropicLiveFactAttribute，
/// 同一組 user-secrets／ANTHROPIC_API_KEY／ANTHROPIC_REQUIRE_LIVE 解析邏輯）。
///
/// 跟 AnthropicLiveApiTests 的分工：那邊釘的是 3 個具體情境的行為斷言（工具選對、
/// 查無資料不編答案、抗注入）；這裡跑的是量化的整份評測集，
/// 產出工具選擇正確率／數字幻覺率／拒答正確率／token 與延遲統計 ——
/// 這些數字才是「Agent 端到端評測 harness」要交付的東西
/// （docs/ml-dl-llm-strengthening-plan-v1.md S4）。
///
/// **這個環境沒有 Anthropic 金鑰，整組測試在這裡是 skip 狀態，數字從未真的跑出來過。**
/// 跟 Phase 0（AnthropicLiveApiTests）欠的是同一筆帳：機制與計分邏輯都驗過
/// （AgentEvaluationHarnessTests 是離線那一半），但「真模型在這 44 題上的真實表現」
/// 要等金鑰才能補上，不能用「應該會過」帶過去。
public class AgentEvaluationTests : IAsyncLifetime
{
    private static readonly DateOnly Today = new(2026, 9, 10);
    private static readonly string Stamp = Today.ToString("yyyyMMdd");

    private SqliteTestDatabase _fixture = null!;
    private ToolDispatcher _dispatcher = null!;

    public async Task InitializeAsync()
    {
        if (AnthropicLiveAvailability.Reason is not null)
        {
            return;   // 整組會被 skip，不必建資料庫
        }

        _fixture = new SqliteTestDatabase();
        var clock = new TestClock(Today);
        await ErpDbSeeder.SeedAsync(_fixture.Db, clock);

        // search_documents 那兩題只驗工具串接，用假 embedding 建索引即可 ——
        // 真實檢索品質已經有 RetrievalQualityTests／RAG_REQUIRE_OLLAMA 那條線在顧。
        var embeddingClient = new FakeEmbeddingClient();
        _dispatcher = TestServices.CreateDispatcher(_fixture, clock, embeddingClient: embeddingClient);
        await TestServices.BuildRagIndexAsync(_fixture, embeddingClient);
    }

    public async Task DisposeAsync()
    {
        if (_fixture is not null)
        {
            await _fixture.DisposeAsync();
        }
    }

    private static AiAssistantOptions LiveOptions() => new()
    {
        ApiKey = AnthropicLiveAvailability.ApiKey,
        TimeoutSeconds = 120   // 真實呼叫 + 多輪工具，比互動式查詢的 60 秒需要更多餘裕
    };

    private (AiAssistantService Service, RecordingLlmClient Recorder) CreateService()
    {
        var options = Options.Create(LiveOptions());
        var recorder = new RecordingLlmClient(new AnthropicLlmClient(options));

        return (new AiAssistantService(
            recorder, _dispatcher, TestServices.CreateConversationStore(), options,
            NullLogger<AiAssistantService>.Instance), recorder);
    }

    [AnthropicLiveFact]
    public async Task 完整評測集_量測工具選擇正確率與數字幻覺率與拒答正確率並產出報表()
    {
        var results = new List<AgentEvaluationHarness.CaseResult>();

        foreach (var evalCase in AgentEvaluationSet.All)
        {
            var (service, recorder) = CreateService();
            results.Add(await AgentEvaluationHarness.RunAsync(service, recorder, evalCase, Stamp));
        }

        var model = LiveOptions().Model;
        var metrics = AgentEvaluationHarness.Aggregate(results, model);
        var report = AgentEvaluationHarness.BuildReport(results, metrics, model);

        WriteReport("agent-eval-full-set", report);

        // 門檻刻意不是 100%：真模型的措辭跟工具選擇每次會有一點浮動，
        // 訂到 100% 只會讓這組測試變成隨時可能無理由變紅的雜訊源，見 AnthropicLiveApiTests 的同一個取捨。
        // 但幻覺率訂在 0——工具沒回傳過的數字出現在答案裡，沒有「可以接受多少」這回事。
        Assert.True(metrics.ToolSelectionAccuracy >= 0.85,
            $"工具選擇正確率 {metrics.ToolSelectionAccuracy:P1} 低於門檻，報表見 docs/verification/");
        Assert.True(metrics.NumericHallucinationRate == 0,
            $"數字幻覺率 {metrics.NumericHallucinationRate:P1} 不是 0，報表見 docs/verification/");
        Assert.True(metrics.RefusalAccuracy >= 0.85,
            $"拒答正確率 {metrics.RefusalAccuracy:P1} 低於門檻，報表見 docs/verification/");
    }

    /// 反向驗證（規劃裡的驗收標準）：把 check_material_sufficiency_for_item 的說明
    /// 改成一句跟它實際功能完全無關的誤導文字，重跑同一批案例，工具選擇正確率必須看得出下降 ——
    /// 不然這組測試量不到「工具描述品質」這件事，正確率再高也不能當證據。
    ///
    /// 沒有走 AiAssistantService：它的 tool-use 迴圈內部固定呼叫
    /// AssistantScope.ToolsFor(role)，唯一能換的是角色而不是任意工具清單，
    /// 這是刻意的（角色過濾是產品邊界，不該留一個後門讓呼叫端夾帶自訂工具定義）。
    /// 所以這裡本地重建同一條 tool-use 迴圈，只是把工具清單換成參數 —— 僅供這個測試使用，
    /// 不是要取代 AiAssistantService。
    [AnthropicLiveFact]
    public async Task 反向驗證_工具描述被改壞後工具選擇正確率會下降()
    {
        var subset = AgentEvaluationSet.All
            .Where(c => c.ExpectedTools.Contains(ToolCatalog.CheckMaterialSufficiency))
            .ToList();
        Assert.NotEmpty(subset);

        var baseline = await RunSubsetAsync(subset, ToolCatalog.All);

        var mutatedTools = ToolCatalog.All
            .Select(t => t.Name == ToolCatalog.CheckMaterialSufficiency
                ? t with { Description = "查詢供應商的付款條件與合約細節，跟料件、庫存、生產完全無關。" }
                : t)
            .ToList();
        var mutated = await RunSubsetAsync(subset, mutatedTools);

        var baselineAccuracy = baseline.Count(r => r.ToolSelectionOk) / (double)baseline.Count;
        var mutatedAccuracy = mutated.Count(r => r.ToolSelectionOk) / (double)mutated.Count;

        WriteReport("agent-eval-reverse-validation", $"""
            ## 反向驗證：check_material_sufficiency_for_item 的說明被改壞

            - 案例數：{subset.Count}
            - 改壞前工具選擇正確率：{baselineAccuracy:P1}
            - 改壞後工具選擇正確率：{mutatedAccuracy:P1}

            {AgentEvaluationHarness.BuildReport(mutated, AgentEvaluationHarness.Aggregate(mutated), LiveOptions().Model)}
            """);

        Assert.True(mutatedAccuracy < baselineAccuracy,
            $"改壞工具描述後正確率（{mutatedAccuracy:P0}）沒有比基準（{baselineAccuracy:P0}）低，"
            + "代表這組測試量不到工具描述品質，形同虛設");
    }

    private async Task<List<AgentEvaluationHarness.CaseResult>> RunSubsetAsync(
        IReadOnlyList<AgentEvalCase> cases, IReadOnlyList<ToolDefinition> tools)
    {
        var results = new List<AgentEvaluationHarness.CaseResult>();

        foreach (var evalCase in cases)
        {
            var recorder = new RecordingLlmClient(new AnthropicLlmClient(Options.Create(LiveOptions())));
            var query = evalCase.ResolveQuery(Stamp);

            var stopwatch = Stopwatch.StartNew();
            var answer = await RunWithToolsAsync(recorder, tools, query);
            stopwatch.Stop();

            results.Add(AgentEvaluationHarness.Score(evalCase, query, recorder, answer, stopwatch.Elapsed));
        }

        return results;
    }

    /// AiAssistantService.AskAsync 的單輪陽春版：不含對話記憶、不含角色過濾，
    /// 只為了能夠注入一份自訂的工具清單。跟正式流程唯一的差異就是這一點。
    private async Task<string> RunWithToolsAsync(
        ILlmClient llmClient, IReadOnlyList<ToolDefinition> tools, string question, CancellationToken ct = default)
    {
        var messages = new List<LlmMessage> { LlmMessage.User(question) };

        for (var iteration = 1; iteration <= 5; iteration++)
        {
            var response = await llmClient.SendAsync(new LlmRequest(AiSystemPrompt.Text, [.. messages], tools), ct);

            if (!response.RequiresToolExecution)
            {
                return string.IsNullOrWhiteSpace(response.Text) ? "（無回覆內容）" : response.Text;
            }

            var toolResults = new List<LlmContentBlock>();
            foreach (var toolUse in response.ToolUses)
            {
                var result = await _dispatcher.ExecuteAsync(toolUse.ToolName, toolUse.Arguments, role: null, ct);
                toolResults.Add(new LlmToolResultBlock(toolUse.ToolUseId, result.Content, result.IsError));
            }

            messages.Add(new LlmMessage(LlmRole.Assistant, response.Content));
            messages.Add(new LlmMessage(LlmRole.User, toolResults));
        }

        return "查詢過程需要的步驟超過上限仍未完成。";
    }

    /// 落地成檔案，理由跟 AnthropicLiveApiTests.WriteTranscript 一樣：
    /// 這件事的價值在於有一份可以被檢視的真實呼叫證據，跑完就散在 console 裡等於沒有。
    private static void WriteReport(string slug, string report)
    {
        var dir = Path.Combine(FindRepositoryRoot(), "docs", "verification");
        Directory.CreateDirectory(dir);

        var stamp = DateTime.Now.ToString("yyyyMMdd");
        var path = Path.Combine(dir, $"{slug}-{stamp}.md");

        File.WriteAllText(path, $"""
            # S4 Agent 端到端評測報表

            - 執行時間：{DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}
            - 端點：api.anthropic.com（正式端點，非假伺服器）
            - 產生方式：`dotnet test tests/Erp.Infrastructure.Tests --filter "FullyQualifiedName~AgentEvaluationTests"`

            {report}
            """);
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("找不到 repository 根目錄");
    }
}
