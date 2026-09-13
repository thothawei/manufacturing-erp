namespace Erp.Infrastructure.AI;

/// 呼叫者的角色。決定看得到、也呼叫得到哪些工具。
///
/// **這是工具層級的邊界，不是資料列層級的隔離。**
/// 允許的工具查得到全庫資料 —— 沒有多租戶、沒有「只能看自己部門的工單」。
/// 真正的多租戶要在每個查詢服務裡下推呼叫者身分並過濾資料列，
/// 那是另一個量級的工程，這裡刻意不做（見 README 的已知限制）。
///
/// 做這一層的理由是它擋得住一個具體的事：agentic 系統把全部能力一視同仁地
/// 攤開給每個呼叫者。工具清單本身就是攻擊面 —— 品保問得到供應商的採購條件，
/// 不需要任何越權技巧，只要問一句就行。
public static class AssistantScope
{
    /// 生管／生產：看生產與物料，不看採購單明細（供應商與交易條件不是生產端的事）
    public const string Production = "production";

    /// 採購：看料件、庫存、MRP 與採購單，不看工單途程細節與品管明細
    public const string Purchasing = "purchasing";

    /// 品保：看料件、庫存、工單進度與品管，不看 MRP 與採購
    public const string Quality = "quality";

    /// 沒指定角色時的行為：全部工具都開。
    ///
    /// 這是刻意的預設，不是疏忽 —— 這個系統本來就沒有身分驗證，
    /// 加一個「預設拒絕」只會讓既有呼叫全部壞掉，卻擋不住任何真的想繞過的人
    /// （他自己填一個角色就好）。角色在這裡的價值是展示邊界怎麼設計，
    /// 不是假裝它是一道認證。這一點在文件裡寫明，不含糊帶過。
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> ToolsByRole =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [Production] = new HashSet<string>(StringComparer.Ordinal)
            {
                ToolCatalog.SearchItems,
                ToolCatalog.GetItemInventoryStatus,
                ToolCatalog.CheckMaterialSufficiency,
                ToolCatalog.GetWorkOrderProgress,
                ToolCatalog.ListWorkOrdersAtRisk,
                ToolCatalog.PredictWorkOrderDelayRisk,
                ToolCatalog.RunMrpShortageAnalysis,
                ToolCatalog.RunMrpTimePhasedAnalysis,
                ToolCatalog.GetQualityInspectionSummary,
                ToolCatalog.SuggestPurchaseOrder,
                ToolCatalog.SearchDocuments
            },

            [Purchasing] = new HashSet<string>(StringComparer.Ordinal)
            {
                ToolCatalog.SearchItems,
                ToolCatalog.GetItemInventoryStatus,
                ToolCatalog.CheckMaterialSufficiency,
                ToolCatalog.ListWorkOrdersAtRisk,
                ToolCatalog.PredictWorkOrderDelayRisk,
                ToolCatalog.RunMrpShortageAnalysis,
                ToolCatalog.RunMrpTimePhasedAnalysis,
                ToolCatalog.ListOpenPurchaseOrders,
                ToolCatalog.SuggestPurchaseOrder,
                ToolCatalog.SearchDocuments
            },

            [Quality] = new HashSet<string>(StringComparer.Ordinal)
            {
                ToolCatalog.SearchItems,
                ToolCatalog.GetItemInventoryStatus,
                ToolCatalog.GetWorkOrderProgress,
                ToolCatalog.GetQualityInspectionSummary,
                ToolCatalog.SearchDocuments
            }
        };

    public static IReadOnlyList<string> KnownRoles => [Production, Purchasing, Quality];

    /// null／空字串代表不限角色。不認得的角色名稱要擲例外而不是預設放行 ——
    /// 打錯字的 "purchase" 靜默變成「全部工具都開」，比直接報錯危險得多。
    public static void EnsureKnown(string? role)
    {
        if (string.IsNullOrWhiteSpace(role) || ToolsByRole.ContainsKey(role))
        {
            return;
        }

        throw new ArgumentException(
            $"未知的角色「{role}」，可用的角色：{string.Join("、", KnownRoles)}", nameof(role));
    }

    /// 這個角色送給 LLM 的工具清單
    public static IReadOnlyList<ToolDefinition> ToolsFor(string? role)
    {
        EnsureKnown(role);

        return string.IsNullOrWhiteSpace(role)
            ? ToolCatalog.All
            : [.. ToolCatalog.All.Where(t => ToolsByRole[role].Contains(t.Name))];
    }

    /// 執行時的第二道檢查。
    ///
    /// 不能只靠「不給 LLM 看到」就當作擋住了：工具名稱是模型自己生成的字串，
    /// 它可以呼叫一個從來沒出現在清單裡的名字（產生一個沒見過的工具名，
    /// 對語言模型來說不需要任何惡意，猜錯就會發生）。
    /// 少了這一層，那個呼叫會照常執行。
    public static bool IsAllowed(string? role, string toolName)
        => string.IsNullOrWhiteSpace(role) || ToolsByRole[role].Contains(toolName);
}
