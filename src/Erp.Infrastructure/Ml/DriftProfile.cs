namespace Erp.Infrastructure.Ml;

/// 一個特徵的漂移偵測基準：分箱邊界 + 訓練集在每一箱的實際比例（S5，見 ml/train.py 的
/// drift_profile）。兩個模型（work-order-delay-model、material-demand-forecast-model）
/// 共用同一個型別，理由跟共用 OnnxDelayRiskModel 的可選模組模式一樣——
/// 「怎麼安全地算漂移分數」這個問題只需要解一次。
///
/// Proportions 不是假設出來的均勻分布：低基數的類別型特徵（例如
/// item_overdue_rate 全資料集只有 5 種不同值）九個分位數切點會切出重複值，
/// 這時候訓練腳本會先去重邊界、再對訓練集重新算一次真正的比例。
/// Proportions.Count 恆等於 Edges.Count + 1。
public sealed record DriftProfile(
    IReadOnlyList<double> Edges,
    IReadOnlyList<double> Proportions);
