namespace Erp.Application.Ml;

/// 一筆模擬的歷史工單：特徵 + 實際有沒有延遲。
/// 欄位刻意用 double?，因為模擬資料**刻意包含缺值** —— 見下方生成規則。
public sealed record HistoricalWorkOrderRecord(
    string WorkOrderNo,
    string ItemCode,
    double? MaterialReadiness,
    double DaysUntilDue,
    double MaxLeadTimeDays,
    double ProgressRatio,
    double ItemOverdueRate,
    double? WeeklyLoadRatio,
    double BomComponentCount,
    bool WasDelayed);

/// 產生訓練用的模擬歷史工單。
///
/// **這是模擬資料，不是真實產線資料。** 這句話不是免責聲明，它決定了整個模組
/// 該怎麼被解讀：下面的生成規則是我自己寫的，所以模型學得回這條規則
/// 幾乎是必然的 —— 那證明的是「pipeline 接起來了」，不是「這個模型對真實產線有效」。
/// 真實資料的訊號會弱得多、雜訊也不是這種長相。文件裡把這一點寫在最前面。
///
/// 那為什麼還要做：JD 要的是「資料清理、特徵工程、模型訓練與評估、落地整合」
/// 這條完整鏈路講不講得清楚。用真實感的假資料把每個決策做過一遍、並誠實標明
/// 哪些結論是資料本身給的、哪些是我設計出來的，比拿一份來路不明的資料集
/// 套一個高分模型更接近實際工作。
///
/// 生成規則（延遲機率）：
///   logit = -1.2
///          + 2.8 × (1 − 齊套率)          缺料是最強的訊號
///          + 0.18 × max(0, 前置期 − 剩餘天數)  補得上就不算風險
///          + 1.1 × max(0, 負載 − 1)      超過基準負載才開始拖累
///          + 1.5 × 品項逾期比例
///          − 1.6 × 完成比例              做得越完整越不會延
///          − 0.05 × 剩餘天數
/// 再取 sigmoid 當作延遲機率，抽一次伯努利。
///
/// 刻意加進去的髒東西（讓資料清理這一步真的有事可做）：
///   - 5% 的標籤翻轉：現場登錄錯誤、或延遲原因與工單無關（颱風、客戶改單）。
///   - 8% 的齊套率缺值：舊系統沒有這個欄位。
///   - 6% 的負載缺值。
///   - 1.5% 的負載離群值（× 8~15）：盤點當天把整月工單一次開出來造成的假尖峰。
///
/// 亂數種子固定，所以同一個 seed 產生的資料完全一樣 ——
/// 訓練結果可重現，這是「評審 clone 下來能跑出同一個模型」的前提。
public sealed class HistoricalWorkOrderGenerator(int seed = 20260913)
{
    private readonly Random _random = new(seed);

    private static readonly string[] ItemCodes = ["TV-100", "MON-200", "TV-200", "MON-100", "TV-300"];

    /// 每個品項各自的逾期比例，讓「品項」這個維度真的有訊號
    private static readonly double[] ItemOverdueRates = [0.18, 0.31, 0.12, 0.42, 0.25];

    public IReadOnlyList<HistoricalWorkOrderRecord> Generate(int count = 1200)
    {
        var records = new List<HistoricalWorkOrderRecord>(count);

        for (var i = 0; i < count; i++)
        {
            var itemIndex = _random.Next(ItemCodes.Length);
            var itemOverdueRate = ItemOverdueRates[itemIndex];

            var readiness = Math.Round(Math.Clamp(_random.NextDouble() * 1.15, 0, 1), 4);
            var daysUntilDue = _random.Next(-6, 25);
            var maxLeadTime = readiness >= 0.999 ? 0 : _random.Next(2, 15);
            var progress = Math.Round(_random.NextDouble(), 4);
            var load = Math.Round(0.5 + (_random.NextDouble() * 1.1), 4);
            var componentCount = _random.Next(2, 9);

            var logit =
                -1.2
                + (2.8 * (1 - readiness))
                + (0.18 * Math.Max(0, maxLeadTime - daysUntilDue))
                + (1.1 * Math.Max(0, load - 1))
                + (1.5 * itemOverdueRate)
                - (1.6 * progress)
                - (0.05 * daysUntilDue);

            var delayed = _random.NextDouble() < Sigmoid(logit);

            // 標籤雜訊：現場登錄錯誤，或延遲原因根本不在這些特徵裡
            if (_random.NextDouble() < 0.05)
            {
                delayed = !delayed;
            }

            // 缺值：舊系統沒有這些欄位
            double? recordedReadiness = _random.NextDouble() < 0.08 ? null : readiness;
            double? recordedLoad = _random.NextDouble() < 0.06 ? null : load;

            // 離群值：盤點當天一次開出整月工單造成的假尖峰
            if (recordedLoad is not null && _random.NextDouble() < 0.015)
            {
                recordedLoad = Math.Round(recordedLoad.Value * (8 + (_random.NextDouble() * 7)), 4);
            }

            records.Add(new HistoricalWorkOrderRecord(
                $"WOH-{i + 1:D5}",
                ItemCodes[itemIndex],
                recordedReadiness,
                daysUntilDue,
                maxLeadTime,
                progress,
                itemOverdueRate,
                recordedLoad,
                componentCount,
                delayed));
        }

        return records;
    }

    /// 輸出成 CSV。欄位順序與 WorkOrderDelayFeatures.FeatureNames 一致，
    /// 訓練腳本直接照名字取用，不必再對一次欄位。
    public static string ToCsv(IReadOnlyList<HistoricalWorkOrderRecord> records)
    {
        var lines = new List<string>(records.Count + 1)
        {
            "work_order_no,item_code," + string.Join(",", WorkOrderDelayFeatures.FeatureNames) + ",was_delayed"
        };

        lines.AddRange(records.Select(r => string.Join(",",
            r.WorkOrderNo,
            r.ItemCode,
            Csv(r.MaterialReadiness),
            Csv(r.DaysUntilDue),
            Csv(r.MaxLeadTimeDays),
            Csv(r.ProgressRatio),
            Csv(r.ItemOverdueRate),
            Csv(r.WeeklyLoadRatio),
            Csv(r.BomComponentCount),
            r.WasDelayed ? "1" : "0")));

        return string.Join("\n", lines) + "\n";
    }

    /// 缺值輸出成空字串而不是 0 或 -1：用哨兵值代表缺值，
    /// 模型會把那個數字當成真的量測結果來學
    private static string Csv(double? value)
        => value?.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) ?? "";

    private static double Sigmoid(double x) => 1.0 / (1.0 + Math.Exp(-x));
}
