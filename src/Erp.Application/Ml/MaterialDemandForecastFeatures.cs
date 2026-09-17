namespace Erp.Application.Ml;

/// 物料週別需求預測模型的輸入特徵（S6）。
///
/// 跟 `WorkOrderDelayFeatures`的差異：那個型別的欄位在 C# 端有唯一的計算方式，
/// 由既有的 BOM／庫存／工單資料即時算出來。這個型別沒有 —— 這個系統目前沒有任何地方
/// 持久化「歷史週別實際需求」，所以這些 lag／rolling 特徵是呼叫端已經算好、直接提供的，
/// 不是這個型別自己從資料庫查出來的。這件事與它的理由記在
/// docs/material-demand-forecast-plan-v1.md。
///
/// 欄位順序就是送進 ONNX 模型的張量順序，由 <see cref="ToVector"/> 固定住，
/// 改動順序會讓模型讀到錯位的數值卻不會報錯（跟 WorkOrderDelayFeatures 同一個風險）。
public sealed record MaterialDemandForecastFeatures(
    /// 前一週的實際需求量
    double Lag1,

    /// 前兩週的實際需求量
    double Lag2,

    /// 前三週的實際需求量
    double Lag3,

    /// 前四週的實際需求量
    double Lag4,

    /// 去年同一週（52 週前）的實際需求量 —— seasonal naive 基準線直接用的就是這個值
    double Lag52,

    /// 最近 4 週的移動平均
    double RollingMean4,

    /// 最近 12 週的移動平均
    double RollingMean12,

    /// 預測目標落在一年裡的第幾週（0~51），捕捉季節性
    int WeekOfYear,

    /// 品項是否為 PANEL-01（one-hot，三個品項目前用 0/1 三欄表示，不是一個類別索引 ——
    /// ONNX 的數值張量沒有類別型別，這樣才不用在 C#/Python 兩邊分別維護一份索引對映）
    double ItemIsPanel01,

    /// 品項是否為 SCREW-05
    double ItemIsScrew05,

    /// 品項是否為 CABLE-07
    double ItemIsCable07)
{
    /// 模型認得的品項。one-hot 只有這三欄，給別的料號一律是全 0 ——
    /// 那不是「這個品項的正確編碼」，是「模型從沒見過的類別」，預測不可信，
    /// 所以呼叫端要先擋下不在這份清單裡的料號，而不是讓它悄悄地被當成全 0 送進模型。
    public static IReadOnlyList<string> KnownItems => ["PANEL-01", "SCREW-05", "CABLE-07"];

    public static IReadOnlyList<string> FeatureNames =>
    [
        "lag_1",
        "lag_2",
        "lag_3",
        "lag_4",
        "lag_52",
        "rolling_mean_4",
        "rolling_mean_12",
        "week_of_year",
        "item_is_panel01",
        "item_is_screw05",
        "item_is_cable07"
    ];

    public float[] ToVector() =>
    [
        (float)Lag1,
        (float)Lag2,
        (float)Lag3,
        (float)Lag4,
        (float)Lag52,
        (float)RollingMean4,
        (float)RollingMean12,
        WeekOfYear,
        (float)ItemIsPanel01,
        (float)ItemIsScrew05,
        (float)ItemIsCable07
    ];

    /// 三個已知品項各自建構 one-hot 特徵的小工具，呼叫端不用自己記三個欄位怎麼填
    public static MaterialDemandForecastFeatures ForItem(
        string itemCode, double lag1, double lag2, double lag3, double lag4, double lag52,
        double rollingMean4, double rollingMean12, int weekOfYear) => new(
        lag1, lag2, lag3, lag4, lag52, rollingMean4, rollingMean12, weekOfYear,
        ItemIsPanel01: itemCode == "PANEL-01" ? 1 : 0,
        ItemIsScrew05: itemCode == "SCREW-05" ? 1 : 0,
        ItemIsCable07: itemCode == "CABLE-07" ? 1 : 0);
}
