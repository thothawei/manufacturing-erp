using System.Text.Json;

namespace Erp.Infrastructure.AI;

/// 所有工具的定義集中在這裡一份。
///
/// 八個工具全部都是唯讀查詢，沒有一個會寫入資料庫
/// （見 docs/ai-assistant-module-plan-v2.md 第 3 節）。
///
/// 每個工具的說明都寫明「數量單位」與「回傳什麼」，這是防幻覺的第一道：
/// 讓 LLM 沒有自行揣測欄位語意的空間。
public static class ToolCatalog
{
    public const string SearchItems = "search_items";
    public const string GetItemInventoryStatus = "get_item_inventory_status";
    public const string CheckMaterialSufficiency = "check_material_sufficiency_for_item";
    public const string GetWorkOrderProgress = "get_work_order_progress";
    public const string ListWorkOrdersAtRisk = "list_work_orders_at_risk";
    public const string RunMrpShortageAnalysis = "run_mrp_shortage_analysis";
    public const string ListOpenPurchaseOrders = "list_open_purchase_orders";
    public const string GetQualityInspectionSummary = "get_quality_inspection_summary";

    public static readonly IReadOnlyList<ToolDefinition> All =
    [
        new ToolDefinition(
            SearchItems,
            """
            以關鍵字搜尋料件主檔，可用料號或品名的部分文字。
            回傳符合的料件清單，每筆包含 item_code（料號）、item_name（品名）、
            item_type（料件類型：FinishedGood 成品／SemiFinished 半成品／RawMaterial 原物料）。
            使用者提到某個產品但沒給精確料號時，先用這個工具確認料號。
            查無資料時回傳空清單。
            """,
            new Dictionary<string, JsonElement>
            {
                ["keyword"] = Schema(new { type = "string", description = "料號或品名的關鍵字" })
            },
            ["keyword"]),

        new ToolDefinition(
            GetItemInventoryStatus,
            """
            查詢單一料件的庫存狀況。必須提供精確料號。
            回傳 on_hand_qty（帳上庫存）、reserved_qty（已被其他工單保留、不可動用）、
            available_qty（可用庫存＝帳上減保留）、unit（單位）、as_of（資料日期）。
            回答「還有多少料可以用」時要用 available_qty，不是 on_hand_qty。
            """,
            new Dictionary<string, JsonElement>
            {
                ["item_code"] = Schema(new { type = "string", description = "精確料號，例如 TV-100" })
            },
            ["item_code"]),

        new ToolDefinition(
            CheckMaterialSufficiency,
            """
            以多階 BOM 展開，計算某個成品用現有庫存最多能做幾個，以及缺哪些原料。
            一律以 available_qty（可用庫存＝帳上減已保留）計算，回傳的 basis 欄位會標明這一點。
            回傳 max_buildable_qty（最多可製造數量）、bom_version（BOM 版本）、
            shortage_components（缺料清單，含 component_code、shortfall_qty 短少數量、
            required_per_finished_unit 每一個成品需要的數量）。
            給了 planned_qty 時，另外回傳 sufficient_for_requested_qty 表示夠不夠做這個數量，
            缺料清單也會以該數量計算。沒給 planned_qty 就是問「最多能做幾個」。
            料件沒有 BOM（例如原物料）時會回報錯誤，這種料件請改用 get_item_inventory_status。
            """,
            new Dictionary<string, JsonElement>
            {
                ["item_code"] = Schema(new { type = "string", description = "成品或半成品的精確料號" }),
                ["planned_qty"] = Schema(new
                {
                    type = "number",
                    description = "想生產的數量。想問「最多能做幾個」時不要帶這個參數"
                })
            },
            ["item_code"]),

        new ToolDefinition(
            GetWorkOrderProgress,
            """
            查詢單張工單的生產進度。必須提供精確工單號。
            回傳 planned_qty（計畫產量）、status（工單狀態）、material_issue_status（發料狀態），
            以及 routing_steps（途程站別清單，依站號排序，每站含 operation_name 工序名稱、
            planned_qty 該站計畫數量、completed_qty 已完工數量、status 站別狀態）。
            實際產出以最後一站的 completed_qty 為準。
            """,
            new Dictionary<string, JsonElement>
            {
                ["work_order_no"] = Schema(new { type = "string", description = "精確工單號，例如 WO-20260910-01" })
            },
            ["work_order_no"]),

        new ToolDefinition(
            ListWorkOrdersAtRisk,
            """
            列出交期落在指定區間內、有延遲風險的未結案工單。不給區間時預設查本週（週一到週日）。
            風險有兩種來源：已逾交期未完工，或剩餘產量的物料不足。
            回傳 due_date（交期）、delay_days（預估延遲天數）、risk_reason（風險原因，
            缺料時會寫明是哪個零件短少多少）。delay_days 為 0 代表有風險但目前還趕得上。
            缺料判定只針對「剩餘待產數量」，已完工的部分不會重複算料。
            """,
            new Dictionary<string, JsonElement>
            {
                ["date_range_start"] = Schema(new { type = "string", description = "起始日期，格式 YYYY-MM-DD" }),
                ["date_range_end"] = Schema(new { type = "string", description = "結束日期，格式 YYYY-MM-DD" })
            },
            []),

        new ToolDefinition(
            RunMrpShortageAnalysis,
            """
            MRP 缺料試算：把規劃期間內所有未結案工單的剩餘產量展開成原料需求，
            扣掉可用庫存與能及時到貨的在途採購，算出還要補多少。已逾期未結案的工單也會納入。
            回傳每個缺料料號的 gross_requirement_qty（毛需求）、available_qty（可用庫存）、
            in_transit_qty（能及時到貨的在途量）、net_shortage_qty（淨缺料量）、
            needed_by_date（需求日期）、suggested_order_qty（建議採購量，已套用最小訂購量與訂購倍量）、
            supplier_code（供應商）、lead_time_days（採購前置期天數）。
            建議採購量直接引用 suggested_order_qty，不要自己從淨缺料量推算。
            """,
            new Dictionary<string, JsonElement>
            {
                ["planning_horizon_days"] = Schema(new
                {
                    type = "integer",
                    description = "規劃期間天數，不給時預設 30 天"
                }),
                ["item_code"] = Schema(new { type = "string", description = "只看單一料號時填入，不填則回傳全部缺料料件" })
            },
            []),

        new ToolDefinition(
            ListOpenPurchaseOrders,
            """
            列出尚未結案的採購單（已下單未到貨、或部分入庫），依預計到貨日排序。
            回傳 po_no（採購單號）、supplier_code（供應商）、item_code（料號）、
            ordered_qty（訂購量）、received_qty（已入庫量）、expected_arrival_date（預計到貨日）、
            status（狀態：Open 未到貨／PartiallyReceived 部分入庫）。
            尚未到貨的數量是 ordered_qty 減 received_qty。已全數入庫的採購單不會出現在這裡。
            """,
            new Dictionary<string, JsonElement>
            {
                ["supplier_code"] = Schema(new { type = "string", description = "只看單一供應商時填入" }),
                ["item_code"] = Schema(new { type = "string", description = "只看單一料號時填入" })
            },
            []),

        new ToolDefinition(
            GetQualityInspectionSummary,
            """
            查詢品管檢驗結果，同一張工單的多次檢驗已由後端彙總成一列。
            回傳 work_order_no（工單號）、item_code（料號）、inspected_qty（檢驗數量）、
            passed_qty（合格數量）、failed_qty（不合格數量）、
            fail_reason_summary（不良原因，多個原因以頓號分隔；全數合格時為 null）。
            不良率請以 failed_qty 除以 inspected_qty 說明，不要自行加總多筆資料。
            """,
            new Dictionary<string, JsonElement>
            {
                ["item_code"] = Schema(new { type = "string", description = "只看單一料號時填入" }),
                ["work_order_no"] = Schema(new { type = "string", description = "只看單一工單時填入" }),
                ["date_range_start"] = Schema(new { type = "string", description = "起始日期，格式 YYYY-MM-DD" }),
                ["date_range_end"] = Schema(new { type = "string", description = "結束日期，格式 YYYY-MM-DD" })
            },
            [])
    ];

    private static JsonElement Schema(object value) => JsonSerializer.SerializeToElement(value);
}
