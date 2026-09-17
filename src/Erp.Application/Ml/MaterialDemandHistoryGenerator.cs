namespace Erp.Application.Ml;

/// 一筆模擬的週別物料需求歷史。
public sealed record WeeklyDemandRecord(
    string ItemCode,
    int WeekIndex,
    DateOnly WeekStart,
    double? DemandQty);

/// 產生訓練用的模擬週別物料需求歷史（S6：物料需求時間序列預測）。
///
/// **這是模擬資料，不是真實產線資料。** 跟 `HistoricalWorkOrderGenerator` 同一個理由、
/// 同一句話放在最前面：下面的生成規則是我自己寫的，模型學得回這條規則幾乎是必然，
/// 證明的是「時間序列這條 pipeline 接起來了、三種方法的對照做得誠實」，
/// 不是「這個預測對真實產線有效」。
///
/// 品項固定用種子資料裡已經有的三個原物料（PANEL-01／SCREW-05／CABLE-07），
/// 讓這個模組跟既有的 MRP／BOM 資料是同一組世界觀，不是憑空編三個新料號。
///
/// 生成規則（單一品項某一週的需求量）：
///   base_level × (1 + seasonal_amplitude × sin(2π × week / 52 + phase))
///              × (1 + trend_per_week × week)
///              + 常態雜訊（標準差 = base_level × noise_ratio）
///   夾在 0 以上（需求不會是負的），四捨五入到整數。
///
/// 三個品項的 base_level／phase／trend 都不同，讓「品項」這個維度真的有訊號 ——
/// 跟延遲風險生成器裡每個品項各自的逾期比例是同一個設計理由。
///
/// 刻意加進去的髒東西：3% 的需求缺值（模擬盤點週或系統停機沒記到）。
/// 沒有加離群值——時間序列預測要驗的是「季節性與趨勢抓不抓得到」，
/// 不是又重講一次資料清理，那件事 `HistoricalWorkOrderGenerator` 已經做過。
///
/// 156 週（3 年）的理由：seasonal naive 需要至少 52 週才有「去年同週」可以參照，
/// 3 年才能切出「前 2 年訓練、最後 1 年當測試集」且測試集本身也滿一個完整季節週期。
public sealed class MaterialDemandHistoryGenerator(int seed = 20260917)
{
    private readonly Random _random = new(seed);

    private static readonly DateOnly HistoryStart = new(2023, 1, 2); // 週一

    private sealed record ItemProfile(
        string ItemCode, double BaseLevel, double SeasonalAmplitude, double PhaseWeeks, double TrendPerWeek);

    // PANEL-01：旺季在年中（面板需求隨顯示器旺季走）、緩步成長。
    // SCREW-05：用量大、波動相對平緩（螺絲不太受季節影響，主要隨產量基期微幅成長）。
    // CABLE-07：波動最大、有輕微衰退（訊號線规格汰換週期短）。
    private static readonly ItemProfile[] Profiles =
    [
        new("PANEL-01", BaseLevel: 220, SeasonalAmplitude: 0.35, PhaseWeeks: 10, TrendPerWeek: 0.0015),
        new("SCREW-05", BaseLevel: 3800, SeasonalAmplitude: 0.12, PhaseWeeks: 0, TrendPerWeek: 0.0008),
        new("CABLE-07", BaseLevel: 340, SeasonalAmplitude: 0.45, PhaseWeeks: 26, TrendPerWeek: -0.0010),
    ];

    public IReadOnlyList<WeeklyDemandRecord> Generate(int weeks = 156)
    {
        var records = new List<WeeklyDemandRecord>(weeks * Profiles.Length);

        foreach (var profile in Profiles)
        {
            for (var week = 0; week < weeks; week++)
            {
                var seasonal = 1 + (profile.SeasonalAmplitude
                    * Math.Sin(2 * Math.PI * ((week + profile.PhaseWeeks) / 52.0)));
                var trend = 1 + (profile.TrendPerWeek * week);
                var mean = profile.BaseLevel * seasonal * trend;

                var noise = NextGaussian() * mean * 0.08;
                var demand = Math.Max(0, Math.Round(mean + noise));

                // 缺值：盤點週或系統停機沒記到，不是需求真的是 0
                double? recordedDemand = _random.NextDouble() < 0.03 ? null : demand;

                records.Add(new WeeklyDemandRecord(
                    profile.ItemCode, week, HistoryStart.AddDays(week * 7), recordedDemand));
            }
        }

        return records;
    }

    /// Box-Muller：C# 的 Random 只給得出均勻分布，雜訊要常態分布才像真實需求的波動
    private double NextGaussian()
    {
        var u1 = 1.0 - _random.NextDouble();
        var u2 = _random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
    }

    public static string ToCsv(IReadOnlyList<WeeklyDemandRecord> records)
    {
        var lines = new List<string>(records.Count + 1) { "item_code,week_index,week_start,demand_qty" };

        lines.AddRange(records.Select(r => string.Join(",",
            r.ItemCode,
            r.WeekIndex,
            r.WeekStart.ToString("yyyy-MM-dd"),
            r.DemandQty?.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) ?? "")));

        return string.Join("\n", lines) + "\n";
    }
}
