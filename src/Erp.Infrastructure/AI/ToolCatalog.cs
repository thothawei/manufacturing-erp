using System.Text.Json;

namespace Erp.Infrastructure.AI;

/// 所有工具的定義集中在這裡一份。
///
/// Phase 2 只接兩個工具，先把 tool-use 迴圈端到端打通；
/// 其餘六個在 Phase 3 補齊（見 docs/ai-assistant-module-plan-v2.md 第 3 節）。
///
/// 每個工具的說明都寫明「數量單位」與「回傳什麼」，這是防幻覺的第一道：
/// 讓 LLM 沒有自行揣測欄位語意的空間。
public static class ToolCatalog
{
    public const string SearchItems = "search_items";
    public const string GetItemInventoryStatus = "get_item_inventory_status";

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
            ["item_code"])
    ];

    private static JsonElement Schema(object value) => JsonSerializer.SerializeToElement(value);
}
