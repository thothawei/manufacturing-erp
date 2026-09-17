using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Erp.Application.Abstractions;
using Erp.Infrastructure.AI;

namespace Erp.Infrastructure.Tests;

/// S4：Agent 端到端評測的計分與報表邏輯。
///
/// 跟 RecordingLlmClient／AnthropicLiveApiTests 分工：那邊負責「怎麼呼叫、怎麼記錄」，
/// 這裡只負責「記錄下來的東西算不算數」，兩者分開才能離線測「計分邏輯本身有沒有算對」——
/// 見 AgentEvaluationHarnessTests，那邊完全不碰真實 API，只餵 FakeLlmClient 的腳本。
internal static class AgentEvaluationHarness
{
    /// 跟 AnthropicLiveApiTests 用同一組「查無」關鍵字，維持判準一致。
    private static readonly string[] NotFoundWords = ["查無", "找不到", "沒有找到", "不存在", "查詢不到", "無此"];

    /// OutOfScope 案例期望講清楚「這不是這些工具能回答的」，用詞不強求一致，
    /// 但意思要落在「拒答／超出範圍」而不是憑自己的知識硬答。
    private static readonly string[] OutOfScopeWords =
        ["無法回答", "不在", "沒有相關工具", "超出", "不是我能", "無法提供", "沒有工具", "系統無法", "不是這個系統", "AI 助理無法"];

    private static readonly Regex NumberPattern = new(@"\d+(?:\.\d+)?", RegexOptions.Compiled);

    /// 2026-09-17 讀自 platform.claude.com/docs/en/about-claude/pricing 的官方牌價（USD / 百萬 token）。
    /// 牌價會變，這裡只是「大概花多少錢」的量級參考，不是帳單依據 ——
    /// 而且預設走 OmniRoute gateway，不是直接打 Anthropic 官方端點，實際計費可能不同
    /// （見 README「AI 助理」）。查不到對應的模型代號時回 null，不亂猜一個價錢。
    private static readonly IReadOnlyDictionary<string, (decimal InputPerMTok, decimal OutputPerMTok)> Pricing =
        new Dictionary<string, (decimal, decimal)>(StringComparer.OrdinalIgnoreCase)
        {
            ["claude-opus-5"] = (5m, 25m),
            ["claude-opus-4-8"] = (5m, 25m),
            ["claude-sonnet-5"] = (2m, 10m),
            ["claude-haiku-4-5"] = (1m, 5m),
        };

    public sealed record CaseResult(
        AgentEvalCase Case,
        string ResolvedQuery,
        string Answer,
        IReadOnlyList<string> ToolNames,
        bool ToolSelectionOk,
        bool RefusalOk,
        IReadOnlyList<string> HallucinatedNumbers,
        long InputTokens,
        long OutputTokens,
        double ElapsedMs);

    public sealed record AgentEvalMetrics(
        int TotalCases,
        double ToolSelectionAccuracy,
        double NumericHallucinationRate,
        double RefusalAccuracy,
        double AvgInputTokens,
        double AvgOutputTokens,
        decimal? EstimatedCostUsd,
        double P50LatencyMs,
        double P95LatencyMs);

    /// 跑一個案例：真的呼叫 AiAssistantService（不論底下是真 LLM 還是 FakeLlmClient），
    /// 量測牆鐘時間，再送去計分。
    public static async Task<CaseResult> RunAsync(
        IAiAssistantService service, RecordingLlmClient recorder, AgentEvalCase evalCase, string stamp,
        CancellationToken ct = default)
    {
        var query = evalCase.ResolveQuery(stamp);
        var stopwatch = Stopwatch.StartNew();
        var answer = await service.AskAsync(query, ct: ct);
        stopwatch.Stop();

        return Score(evalCase, query, recorder, answer.Answer, stopwatch.Elapsed);
    }

    public static CaseResult Score(
        AgentEvalCase evalCase, string resolvedQuery, RecordingLlmClient recorder, string answer, TimeSpan elapsed)
    {
        var toolNames = recorder.ToolUses.Select(t => t.ToolName).ToList();

        var toolSelectionOk = evalCase.ExpectedTools.Count == 0
            || evalCase.ExpectedTools.All(toolNames.Contains);

        var refusalOk = !evalCase.ExpectRefusal || IsRefusal(evalCase, answer);

        var hallucinated = FindHallucinatedNumbers(recorder, answer);

        var inputTokens = recorder.Rounds.Sum(r => r.Response.Usage?.InputTokens ?? 0);
        var outputTokens = recorder.Rounds.Sum(r => r.Response.Usage?.OutputTokens ?? 0);

        return new CaseResult(
            evalCase, resolvedQuery, answer, toolNames, toolSelectionOk, refusalOk,
            hallucinated, inputTokens, outputTokens, elapsed.TotalMilliseconds);
    }

    private static bool IsRefusal(AgentEvalCase evalCase, string answer)
    {
        var keywords = evalCase.Kind == AgentEvalCaseKind.OutOfScope ? OutOfScopeWords : NotFoundWords;
        return keywords.Any(w => answer.Contains(w, StringComparison.Ordinal));
    }

    /// 幻覺數字偵測：答案裡出現的每個數字，拿去跟這次對話「所有工具回傳的 JSON 原文」
    /// 做子字串比對，不在裡面的就是模型自己編出來的 —— 程式比對，不用 LLM-as-judge
    /// （見 docs/ml-dl-llm-strengthening-plan-v1.md S4：這是規劃裡明講不准用 judge 的那個維度）。
    ///
    /// 誠實的已知限制：這是字面子字串比對，不是逐欄位語意比對。
    /// 料號／工單號本身含數字（TV-100 的「100」）時，答案提到同一個數字會被判定「找得到依據」，
    /// 即使模型講的其實是別的意思 —— 但這個方向是保守的：只會讓算出來的幻覺率比實際更低，
    /// 不會把真正編出來的數字漏標成沒事，因為真編出來的數字不會剛好撞上任何工具回傳過的文字。
    private static IReadOnlyList<string> FindHallucinatedNumbers(RecordingLlmClient recorder, string answer)
    {
        var toolResultText = string.Join(
            '\n',
            recorder.Rounds
                .SelectMany(r => r.Request.Messages)
                .SelectMany(m => m.Content)
                .OfType<LlmToolResultBlock>()
                .Select(b => b.Content));

        return [.. NumberPattern.Matches(answer)
            .Select(m => m.Value)
            .Distinct()
            .Where(number => !toolResultText.Contains(number, StringComparison.Ordinal))];
    }

    public static AgentEvalMetrics Aggregate(IReadOnlyList<CaseResult> results, string? model = null)
    {
        var withExpectedTools = results.Where(r => r.Case.ExpectedTools.Count > 0).ToList();
        var toolSelectionAccuracy = withExpectedTools.Count == 0
            ? 1.0
            : withExpectedTools.Count(r => r.ToolSelectionOk) / (double)withExpectedTools.Count;

        var hallucinationRate = results.Count == 0
            ? 0.0
            : results.Count(r => r.HallucinatedNumbers.Count > 0) / (double)results.Count;

        var refusalCases = results.Where(r => r.Case.ExpectRefusal).ToList();
        var refusalAccuracy = refusalCases.Count == 0
            ? 1.0
            : refusalCases.Count(r => r.RefusalOk) / (double)refusalCases.Count;

        var totalInput = results.Sum(r => r.InputTokens);
        var totalOutput = results.Sum(r => r.OutputTokens);
        var avgInput = results.Count == 0 ? 0 : totalInput / (double)results.Count;
        var avgOutput = results.Count == 0 ? 0 : totalOutput / (double)results.Count;

        var latencies = results.Select(r => r.ElapsedMs).Order().ToList();

        return new AgentEvalMetrics(
            results.Count,
            toolSelectionAccuracy,
            hallucinationRate,
            refusalAccuracy,
            avgInput,
            avgOutput,
            model is null ? null : EstimateCostUsd(model, totalInput, totalOutput),
            Percentile(latencies, 50),
            Percentile(latencies, 95));
    }

    public static decimal? EstimateCostUsd(string model, long inputTokens, long outputTokens)
    {
        var key = model.Contains('/') ? model[(model.LastIndexOf('/') + 1)..] : model;

        if (!Pricing.TryGetValue(key, out var price))
        {
            return null;
        }

        return inputTokens / 1_000_000m * price.InputPerMTok + outputTokens / 1_000_000m * price.OutputPerMTok;
    }

    private static double Percentile(IReadOnlyList<double> sortedValues, double p)
    {
        if (sortedValues.Count == 0)
        {
            return 0;
        }

        var index = Math.Clamp((int)Math.Ceiling(p / 100.0 * sortedValues.Count) - 1, 0, sortedValues.Count - 1);
        return sortedValues[index];
    }

    /// 報表可一鍵產生：這個方法就是那個一鍵——給一組跑完的結果就能出一份 markdown，
    /// 不需要另外手動整理。AgentEvaluationTests 會把它寫進 docs/verification/。
    public static string BuildReport(IReadOnlyList<CaseResult> results, AgentEvalMetrics metrics, string model)
    {
        var sb = new StringBuilder();

        sb.AppendLine("## 整體指標");
        sb.AppendLine();
        sb.AppendLine("| 指標 | 數值 |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| 案例數 | {metrics.TotalCases} |");
        sb.AppendLine($"| 工具選擇正確率 | {metrics.ToolSelectionAccuracy:P1} |");
        sb.AppendLine($"| 數字幻覺率（至少一個數字查無依據的案例比例） | {metrics.NumericHallucinationRate:P1} |");
        sb.AppendLine($"| 拒答正確率（含查無資料與超出範圍） | {metrics.RefusalAccuracy:P1} |");
        sb.AppendLine($"| 平均 input tokens | {metrics.AvgInputTokens:F0} |");
        sb.AppendLine($"| 平均 output tokens | {metrics.AvgOutputTokens:F0} |");
        sb.AppendLine(
            $"| 估計成本（{model}，官方牌價，OmniRoute 實際計費可能不同） | "
            + (metrics.EstimatedCostUsd is { } cost ? $"US${cost:F4}" : "無牌價資料，未計算") + " |");
        sb.AppendLine($"| p50 延遲 | {metrics.P50LatencyMs:F0} ms |");
        sb.AppendLine($"| p95 延遲 | {metrics.P95LatencyMs:F0} ms |");
        sb.AppendLine();

        sb.AppendLine("## 依類別分解");
        sb.AppendLine();
        sb.AppendLine("| 類別 | 案例數 | 工具選擇正確率 | 拒答正確率 |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var kind in Enum.GetValues<AgentEvalCaseKind>())
        {
            var group = results.Where(r => r.Case.Kind == kind).ToList();
            if (group.Count == 0)
            {
                continue;
            }

            var withExpected = group.Where(r => r.Case.ExpectedTools.Count > 0).ToList();
            var toolAcc = withExpected.Count == 0 ? "—" : $"{withExpected.Count(r => r.ToolSelectionOk) / (double)withExpected.Count:P0}";

            var refusalGroup = group.Where(r => r.Case.ExpectRefusal).ToList();
            var refusalAcc = refusalGroup.Count == 0 ? "—" : $"{refusalGroup.Count(r => r.RefusalOk) / (double)refusalGroup.Count:P0}";

            sb.AppendLine($"| {kind} | {group.Count} | {toolAcc} | {refusalAcc} |");
        }
        sb.AppendLine();

        var failures = results.Where(r => !r.ToolSelectionOk || !r.RefusalOk || r.HallucinatedNumbers.Count > 0).ToList();
        sb.AppendLine($"## 失敗案例（{failures.Count} / {results.Count}）");
        sb.AppendLine();

        if (failures.Count == 0)
        {
            sb.AppendLine("無。");
        }
        else
        {
            foreach (var f in failures)
            {
                sb.AppendLine($"### {f.Case.Kind}：{f.ResolvedQuery}");
                sb.AppendLine();
                sb.AppendLine($"- 期望工具：{string.Join("、", f.Case.ExpectedTools)}");
                sb.AppendLine($"- 實際呼叫：{string.Join("、", f.ToolNames)}");
                sb.AppendLine($"- 工具選擇正確：{f.ToolSelectionOk}");
                sb.AppendLine($"- 拒答正確：{f.RefusalOk}（期望拒答：{f.Case.ExpectRefusal}）");
                sb.AppendLine($"- 疑似幻覺數字：{(f.HallucinatedNumbers.Count == 0 ? "無" : string.Join("、", f.HallucinatedNumbers))}");
                sb.AppendLine($"- 回答：{f.Answer}");
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }
}
