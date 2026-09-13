namespace Erp.Application.Ml;

/// 工單延遲風險模型的輸入特徵。
///
/// **這個型別是特徵定義的唯一真相**：訓練資料由它產生，線上推論也由它組出來。
/// 分成兩份寫（例如訓練用 Python 算、線上用 C# 算）是 training/serving skew
/// 最典型的來源 —— 兩邊對「齊套率」的定義差一點點，模型上線後的表現就會
/// 與離線評估對不上，而且很難查。
///
/// 欄位順序就是送進 ONNX 模型的張量順序，由 <see cref="ToVector"/> 固定住，
/// 改動順序會讓模型讀到錯位的數值卻不會報錯（`FeatureOrderTests` 釘住這件事）。
public sealed record WorkOrderDelayFeatures(
    /// 物料齊套率：剩餘產量所需的原料，可用庫存覆蓋得了的比例（0~1，1 代表全齊）
    double MaterialReadiness,

    /// 距離交期還有幾天。已逾期是負數 —— 不夾成 0，逾期程度本身就是訊號
    double DaysUntilDue,

    /// 缺料件裡最長的採購前置期（天）。沒缺料時是 0
    double MaxLeadTimeDays,

    /// 工單完成比例（0~1），以途程最後一站的完工數量除以計畫產量
    double ProgressRatio,

    /// 這個品項目前未結案工單中，已經逾交期的比例（0~1）。
    ///
    /// 原本這裡放的是「歷史延遲率」，寫到線上推論才發現**算不出來** ——
    /// 系統沒有記錄工單的實際完工日，只有交期與狀態，所以「當初有沒有準時完工」
    /// 這件事在資料裡根本不存在。特徵工程的第一個判準是「預測當下拿不拿得到」，
    /// 不是「有沒有預測力」；一個離線算得出來、線上算不出來的特徵，
    /// 離線評估會很好看，上線就是空的。換成這個語意相近、而且現在就查得到的替代品。
    double ItemOverdueRate,

    /// 交期當週的未結案工單數除以產線基準負載。1 以上代表滿載
    double WeeklyLoadRatio,

    /// BOM 展開後的葉節點原料件數，當作結構複雜度的代理變數
    double BomComponentCount)
{
    /// 送進模型的欄位順序。**不要改動這個順序**：
    /// ONNX 模型吃的是一個沒有欄位名稱的浮點數張量，順序錯了它會照算不誤，
    /// 只是算出來的機率毫無意義。
    public static IReadOnlyList<string> FeatureNames =>
    [
        "material_readiness",
        "days_until_due",
        "max_lead_time_days",
        "progress_ratio",
        "item_overdue_rate",
        "weekly_load_ratio",
        "bom_component_count"
    ];

    public float[] ToVector() =>
    [
        (float)MaterialReadiness,
        (float)DaysUntilDue,
        (float)MaxLeadTimeDays,
        (float)ProgressRatio,
        (float)ItemOverdueRate,
        (float)WeeklyLoadRatio,
        (float)BomComponentCount
    ];
}
