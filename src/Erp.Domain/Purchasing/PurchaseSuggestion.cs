namespace Erp.Domain.Purchasing;

public enum PurchaseSuggestionStatus
{
    /// 等待人工確認。這是 AI 唯一能寫出來的狀態
    PendingApproval,

    /// 已核准，並且已經產生對應的正式採購單
    Approved,

    /// 已駁回，不會產生採購單
    Rejected
}

/// 待人工確認的採購建議。
///
/// **這是整個系統裡唯一由 AI 寫入的東西，而它刻意不是採購單。**
/// AI 能做的是「產生一筆建議」，把建議變成正式採購單需要人按下核准 ——
/// 即使 LLM 已經被限制只能呼叫工具、不能直接寫 SQL，寫入類的操作仍然多一層人工確認，
/// 這是目前 agentic 系統設計的業界共識模式。
///
/// 理由不是「怕 LLM 出錯」這麼籠統：採購會產生對外的金錢承諾，
/// 而 LLM 的輸入（使用者的一句話、檢索到的文件內容）都是它控制不了的。
/// 把「產生建議」與「成立承諾」分開，出錯的最壞結果就只是多一筆要被駁回的建議。
public sealed class PurchaseSuggestion
{
    public required string SuggestionNo { get; init; }
    public required string ItemCode { get; init; }

    /// 建議採購量。直接沿用 MRP 算出的 suggested_order_qty（已套用最小訂購量與訂購倍量）
    public required decimal SuggestedQty { get; init; }

    public string? SupplierCode { get; init; }
    public required DateOnly NeededByDate { get; init; }

    /// 為什麼建議這一筆 —— 由後端從 MRP 結果組出來，不是 LLM 寫的
    public required string Reason { get; init; }

    public required DateOnly CreatedOn { get; init; }

    public PurchaseSuggestionStatus Status { get; set; } = PurchaseSuggestionStatus.PendingApproval;

    /// 核准或駁回的人。沒有身分驗證，所以這是呼叫端自己填的 ——
    /// 它是一筆稽核紀錄，不是一道權限檢查（README 的已知限制有寫明）。
    public string? DecidedBy { get; set; }
    public DateOnly? DecidedOn { get; set; }

    /// 核准後產生的正式採購單號。駁回或待審時為 null
    public string? CreatedPoNo { get; set; }

    public bool IsPending => Status == PurchaseSuggestionStatus.PendingApproval;
}
