namespace Erp.Infrastructure.Rag;

/// 標註的查詢類別。斷言強度按類別分開 —— 用同一條標準去要求
/// 「同義詞改寫」與「跨文件混合主題」，只會逼人把標準調到最鬆的那個。
public enum QueryKind
{
    /// 用文件裡出現過的詞問，最基本的一組
    Direct,

    /// 換句話說：用現場口語而不是文件用詞（「色偏」→「顏色不均」）。
    /// embedding 真正該會的事，關鍵字比對做不到的也是這個。
    Paraphrase,

    /// 答案橫跨兩份文件，或兩份文件都沾得上邊
    CrossDocument,

    /// 主題沾得上邊、但語料裡根本沒寫的事（售價、付款條件、賠償金額）。
    /// 這類最危險：分數不會低到被門檻擋下，但回什麼都是錯的。
    OutOfScope,

    /// 與語料完全無關
    Irrelevant
}

public sealed record LabelledQuery(
    string Query,
    QueryKind Kind,
    /// 應該被檢索到的來源文件；OutOfScope 與 Irrelevant 沒有正確答案
    string? ExpectedSource = null,
    /// CrossDocument 時的第二個可接受來源
    string? AlternativeSource = null);

/// 檢索品質的標註評測集。
///
/// 原本只有 8 組（5 相關 + 3 無關）。那個量級足以擋住「換到一個在中文語料上
/// 不可用的模型」這種級別的退化，擋不住細微的品質下滑 —— 8 組題目就是 8 組題目，
/// 少一題命中就掉 12.5 個百分點，分數本身沒有解析度。
///
/// 這份擴到 30 組，並把題目分類：每份文件至少三題，其中一題必須是換句話說
/// （用現場口語而不是文件用詞），另外補上「主題沾得上邊但語料沒寫」的邊界題 ——
/// 那類問題最危險，分數不會低到被門檻擋下，但回什麼都是錯的。
///
/// 標註原則：一題只標一個正確來源（跨文件的才標第二個），標的是
/// 「這個問題的答案寫在哪份文件裡」，不是「哪份文件看起來比較像」。
public static class RetrievalEvaluationSet
{
    private const string ColorSop = "品管異常處理 SOP — 面板色偏";
    private const string NoiseSop = "品管異常處理 SOP — 組裝異音";
    private const string DimensionSop = "品管異常處理 SOP — 外觀尺寸超差";
    private const string LaminatorManual = "設備維修手冊摘要 — 面板貼合機";
    private const string ScrewdriverManual = "設備維修手冊摘要 — 自動鎖螺絲機";
    private const string ColorComplaint = "客訴處理紀錄 — 面板色偏批量客訴";
    private const string CableComplaint = "客訴處理紀錄 — 訊號線接觸不良";

    public static readonly IReadOnlyList<LabelledQuery> All =
    [
        // ── 品管異常處理 SOP — 面板色偏 ──
        new("面板色偏怎麼判定？允收標準是多少？", QueryKind.Direct, ColorSop),
        new("螢幕顏色不均勻要怎麼量？標準是什麼？", QueryKind.Paraphrase, ColorSop),
        new("色偏判定常見的誤判原因有哪些？", QueryKind.Direct, ColorSop),

        // ── 品管異常處理 SOP — 組裝異音 ──
        new("組裝後有異音，先查什麼？", QueryKind.Direct, NoiseSop),
        new("電視搖起來有喀喀的聲音，可能是什麼問題？", QueryKind.Paraphrase, NoiseSop),
        new("異音重工之後要重新做哪幾站檢驗？", QueryKind.Direct, NoiseSop),

        // ── 品管異常處理 SOP — 外觀尺寸超差 ──
        new("機殼尺寸超差要退料還是挑選使用？", QueryKind.Direct, DimensionSop),
        new("機殼外框的公差是多少？用什麼量？", QueryKind.Direct, DimensionSop),
        new("同一台重工好幾次還是有聲音，該換個方向查什麼？",
            QueryKind.CrossDocument, DimensionSop, NoiseSop),

        // ── 設備維修手冊摘要 — 面板貼合機 ──
        new("貼合機加熱板壞了要等多久才有零件？", QueryKind.Direct, LaminatorManual),
        new("貼合的位置跑掉了要怎麼校正？要花多久？", QueryKind.Paraphrase, LaminatorManual),
        new("貼合機停機會不會影響工單交期？", QueryKind.Direct, LaminatorManual),

        // ── 設備維修手冊摘要 — 自動鎖螺絲機 ──
        new("鎖螺絲的標準扭力是多少？多久校驗一次？", QueryKind.Direct, ScrewdriverManual),
        new("螺絲有時候會沒鎖到，通常是什麼原因？", QueryKind.Paraphrase, ScrewdriverManual),
        new("自動鎖螺絲機扭力跑掉了先查什麼？", QueryKind.Direct, ScrewdriverManual),

        // ── 客訴處理紀錄 — 面板色偏批量客訴 ──
        new("以前有過面板色偏的批量客訴嗎？怎麼處理的？", QueryKind.Direct, ColorComplaint),
        new("客戶抱怨過畫面整片偏藍嗎？後來怎麼改的？", QueryKind.Paraphrase, ColorComplaint),
        new("加嚴面板檢驗標準之後，對採購跟備料有什麼影響？", QueryKind.Direct, ColorComplaint),

        // ── 客訴處理紀錄 — 訊號線接觸不良 ──
        new("訊號線接觸不良的客訴是怎麼改善的？", QueryKind.Direct, CableComplaint),
        new("客戶說用一陣子畫面會閃、晃一下就沒訊號，以前發生過嗎？",
            QueryKind.Paraphrase, CableComplaint),
        new("線材插到底沒有明確判準會造成什麼後果？",
            QueryKind.CrossDocument, CableComplaint, NoiseSop),

        // ── 主題沾得上邊，但語料裡根本沒寫 ──
        new("TV-100 的建議售價是多少？", QueryKind.OutOfScope),
        new("供應商 SUP-008 的付款條件是什麼？", QueryKind.OutOfScope),
        new("貼合機這台設備買進來多少錢？", QueryKind.OutOfScope),
        new("面板色偏那次客訴最後賠了客戶多少錢？", QueryKind.OutOfScope),

        // ── 與語料完全無關 ──
        new("今天台北天氣如何？", QueryKind.Irrelevant),
        new("幫我寫一個 Python 的 for 迴圈", QueryKind.Irrelevant),
        new("下一季的匯率走勢怎麼看？", QueryKind.Irrelevant),
        new("推薦幾家台北好吃的日式料理", QueryKind.Irrelevant),
        new("勞健保的投保級距是怎麼算的？", QueryKind.Irrelevant),
    ];

    public static IEnumerable<LabelledQuery> OfKind(params QueryKind[] kinds)
        => All.Where(q => kinds.Contains(q.Kind));

    /// 有正確答案的那些（Direct／Paraphrase／CrossDocument）
    public static IEnumerable<LabelledQuery> Answerable
        => OfKind(QueryKind.Direct, QueryKind.Paraphrase, QueryKind.CrossDocument);
}
