namespace Erp.Infrastructure.Tests;

/// 案例類別。跟 RetrievalEvaluationSet 的 QueryKind 是同一個精神：
/// 分類記的是「這一題想戳破什麼」，不是單純的難易度標籤。
public enum AgentEvalCaseKind
{
    /// 一句話對應一個工具，最基本的一組
    SingleTool,

    /// 一句話要跨兩個以上的工具家族才答得完整（例如同時問可製造量與延遲風險）
    MultiTool,

    /// 使用者講的是產品俗名而不是精確料號，模型得先用 search_items 消歧
    Ambiguous,

    /// 問的是不存在的料號／工單號，正確行為是「查過之後老實說查無」，不是編一個
    Refusal,

    /// 語料與工具都答不了的問題，正確行為是說明「這不是這個系統能回答的」
    OutOfScope
}

/// 一組端到端案例：問題、屬於哪個類別、期望被呼叫到的工具、以及是否期望模型拒答／坦承查無。
///
/// 刻意不在這裡放「期望的數字」——那些數字只能由模型在對話當下真正呼叫工具、
/// 拿到後端算出的 JSON 才算數（見 docs/ml-dl-llm-strengthening-plan-v1.md S4）。
/// 硬把一個數字寫進這個檔案，跟 LLM 隨口猜中是同一種假陽性；
/// AgentEvaluationHarness 驗的是「答案裡的每個數字都能在這次對話的工具結果裡找到」，
/// 而工具結果本身就是「用既有 API 現場算出來的」，不需要另外複製一份。
public sealed record AgentEvalCase(
    string Query,
    AgentEvalCaseKind Kind,
    IReadOnlyList<string> ExpectedTools,
    bool ExpectRefusal = false)
{
    /// 工單號是「WO-{今天的日期}-序號」，種子資料每次重建都用執行當天算，
    /// 案例集不能寫死日期，所以用 {stamp} 佔位，執行前換成當次的 yyyyMMdd。
    public string ResolveQuery(string stamp) => Query.Replace("{stamp}", stamp);
}

/// S4：Agent 端到端評測案例集。
///
/// 情境與 AiAssistantScenarioTests／AnthropicLiveApiTests 共用同一份種子資料
/// （ErpDbSeeder，today = 2026-09-10，見 AgentEvaluationTests 的 Today 常數），
/// 料號、工單號、供應商代號全部對得上：
///   TV-100（成品）├ PANEL-01×2（原料，現有 SUP-008，前置期 5 天）
///                 ├ CHASSIS-02×3（半成品）─ SCREW-05×4（原料，SUP-021）
///                 └ CABLE-07×1（原料，SUP-015）
///   MON-200（成品）├ PANEL-01×1 └ CABLE-07×2
///   WO-{stamp}-01：TV-100 100 台，3 天後到期，缺料風險
///   WO-{stamp}-02：MON-200 30 台，已逾期 2 天，已全數發料，有品管紀錄
///   WO-{stamp}-03：MON-200 30 台，45 天後到期（遠期，不該被本週風險/預設 MRP 撈到）
///   WO-{stamp}-04：TV-100 20 台，已完工，提供品管歷史
///
/// SearchDocuments 那兩題刻意只驗「工具串接有沒有動」——這個 harness 用的 dispatcher
/// 接的是 FakeEmbeddingClient（真實 Ollama 語意品質已經有 RetrievalQualityTests 那條線在顧），
/// 所以不對檢索到的段落內容做強斷言。
public static class AgentEvaluationSet
{
    public static readonly IReadOnlyList<AgentEvalCase> All =
    [
        // ── SingleTool：search_items ──
        new("料號裡有沒有跟「面板」有關的東西？", AgentEvalCaseKind.SingleTool, ["search_items"]),
        new("幫我找一下料號開頭是 TV 的品項", AgentEvalCaseKind.SingleTool, ["search_items"]),

        // ── SingleTool：get_item_inventory_status ──
        new("TV-100 現在可用庫存多少？", AgentEvalCaseKind.SingleTool, ["get_item_inventory_status"]),
        new("PANEL-01 帳上庫存跟可用庫存差多少？", AgentEvalCaseKind.SingleTool, ["get_item_inventory_status"]),
        new("CABLE-07 還有多少條？", AgentEvalCaseKind.SingleTool, ["get_item_inventory_status"]),

        // ── SingleTool：check_material_sufficiency_for_item ──
        new("TV-100 用現有庫存最多可以做幾台？", AgentEvalCaseKind.SingleTool, ["check_material_sufficiency_for_item"]),
        new("MON-200 現在最多能生產幾台？", AgentEvalCaseKind.SingleTool, ["check_material_sufficiency_for_item"]),
        new("TV-100 要做 100 台，現在的庫存夠不夠？",
            AgentEvalCaseKind.SingleTool, ["check_material_sufficiency_for_item"]),

        // ── SingleTool：get_work_order_progress ──
        new("這個月第一張工單（WO-{stamp}-01）做到哪了？", AgentEvalCaseKind.SingleTool, ["get_work_order_progress"]),
        new("WO-{stamp}-02 目前進度如何？發料了沒？", AgentEvalCaseKind.SingleTool, ["get_work_order_progress"]),

        // ── SingleTool：list_work_orders_at_risk ──
        new("這週有哪些工單有延遲風險？", AgentEvalCaseKind.SingleTool, ["list_work_orders_at_risk"]),
        new("未來 30 天內有哪些工單可能delay？", AgentEvalCaseKind.SingleTool, ["list_work_orders_at_risk"]),

        // ── SingleTool：run_mrp_shortage_analysis ──
        new("目前 MRP 試算下來，缺料最嚴重的是哪些料號？", AgentEvalCaseKind.SingleTool, ["run_mrp_shortage_analysis"]),
        new("PANEL-01 這個料號 MRP 算出來要補多少？", AgentEvalCaseKind.SingleTool, ["run_mrp_shortage_analysis"]),

        // ── SingleTool：run_mrp_time_phased_analysis ──
        new("PANEL-01 大概什麼時候會開始缺料？", AgentEvalCaseKind.SingleTool, ["run_mrp_time_phased_analysis"]),
        new("幫我看未來 4 週的物料水位變化，會不會轉負？",
            AgentEvalCaseKind.SingleTool, ["run_mrp_time_phased_analysis"]),

        // ── SingleTool：list_open_purchase_orders ──
        new("現在有哪些採購單還沒到貨？", AgentEvalCaseKind.SingleTool, ["list_open_purchase_orders"]),
        new("PANEL-01 有沒有在途的採購單？", AgentEvalCaseKind.SingleTool, ["list_open_purchase_orders"]),

        // ── SingleTool：get_quality_inspection_summary ──
        new("WO-{stamp}-02 的品管檢驗結果如何？", AgentEvalCaseKind.SingleTool, ["get_quality_inspection_summary"]),
        new("WO-{stamp}-04 這張工單的不良率大概多少？", AgentEvalCaseKind.SingleTool, ["get_quality_inspection_summary"]),

        // ── SingleTool：predict_work_order_delay_risk ──
        new("WO-{stamp}-01 用模型預測的延遲機率是多少？",
            AgentEvalCaseKind.SingleTool, ["predict_work_order_delay_risk"]),
        new("WO-{stamp}-02 規則判斷跟模型判斷的延遲風險一致嗎？",
            AgentEvalCaseKind.SingleTool, ["predict_work_order_delay_risk"]),

        // ── SingleTool：suggest_purchase_order（唯一的寫入工具）──
        new("缺料的部分幫我開採購建議", AgentEvalCaseKind.SingleTool, ["suggest_purchase_order"]),
        new("PANEL-01 幫我提一個採購建議", AgentEvalCaseKind.SingleTool, ["suggest_purchase_order"]),

        // ── SingleTool：search_documents（只驗工具串接，不驗檢索品質）──
        new("面板色偏要怎麼判定？允收標準是什麼？", AgentEvalCaseKind.SingleTool, ["search_documents"]),
        new("組裝後有異音，SOP 上要先查什麼？", AgentEvalCaseKind.SingleTool, ["search_documents"]),

        // ── MultiTool：一句話橫跨兩個以上工具家族 ──
        new("TV-100 用現有庫存最多可以做幾台？另外這週有哪些工單有延遲風險？",
            AgentEvalCaseKind.MultiTool, ["check_material_sufficiency_for_item", "list_work_orders_at_risk"]),
        new("PANEL-01 現在缺多少？有沒有在途採購可以補上？",
            AgentEvalCaseKind.MultiTool, ["run_mrp_shortage_analysis", "list_open_purchase_orders"]),
        new("WO-{stamp}-01 現在進度到哪，延遲風險模型怎麼判斷？",
            AgentEvalCaseKind.MultiTool, ["get_work_order_progress", "predict_work_order_delay_risk"]),
        new("WO-{stamp}-02 的品管結果如何？這張工單進度到哪了？",
            AgentEvalCaseKind.MultiTool, ["get_quality_inspection_summary", "get_work_order_progress"]),
        new("PANEL-01 有在途採購嗎？缺料的話幫我開採購建議",
            AgentEvalCaseKind.MultiTool, ["list_open_purchase_orders", "suggest_purchase_order"]),
        new("面板色偏怎麼判定？順便查一下 PANEL-01 現在可用庫存多少",
            AgentEvalCaseKind.MultiTool, ["search_documents", "get_item_inventory_status"]),

        // ── Ambiguous：講的是俗名，得先消歧再查 ──
        new("螢幕現在庫存還有多少？",
            AgentEvalCaseKind.Ambiguous, ["search_items", "get_item_inventory_status"]),
        new("電視最多能做幾台？",
            AgentEvalCaseKind.Ambiguous, ["search_items", "check_material_sufficiency_for_item"]),
        new("訊號線還夠嗎？",
            AgentEvalCaseKind.Ambiguous, ["search_items", "get_item_inventory_status"]),

        // ── Refusal：問不存在的料號／工單號，正確行為是「查過後老實說查無」 ──
        new("TV-999 這個料號現在庫存多少？",
            AgentEvalCaseKind.Refusal, ["get_item_inventory_status"], ExpectRefusal: true),
        new("WO-20200101-99 這張工單進度如何？",
            AgentEvalCaseKind.Refusal, ["get_work_order_progress"], ExpectRefusal: true),
        new("ABC-999 這個料號最多能做幾個？",
            AgentEvalCaseKind.Refusal, ["check_material_sufficiency_for_item"], ExpectRefusal: true),
        new("WO-20200101-99 用模型預測延遲機率是多少？",
            AgentEvalCaseKind.Refusal, ["predict_work_order_delay_risk"], ExpectRefusal: true),
        new("ABC-999 幫我開一張採購建議",
            AgentEvalCaseKind.Refusal, ["suggest_purchase_order"], ExpectRefusal: true),

        // ── OutOfScope：語料與工具都答不了 ──
        new("今天台北天氣如何？", AgentEvalCaseKind.OutOfScope, [], ExpectRefusal: true),
        new("幫我寫一個 Python 的 for 迴圈", AgentEvalCaseKind.OutOfScope, [], ExpectRefusal: true),
        new("牛頓第二定律是什麼？", AgentEvalCaseKind.OutOfScope, [], ExpectRefusal: true),
        new("推薦幾家台北好吃的日式料理", AgentEvalCaseKind.OutOfScope, [], ExpectRefusal: true),
    ];

    public static IEnumerable<AgentEvalCase> OfKind(params AgentEvalCaseKind[] kinds)
        => All.Where(c => kinds.Contains(c.Kind));
}
