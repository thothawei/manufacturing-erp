namespace Erp.Application.Bom;

/// 多階 BOM 攤平後的葉節點需求。
/// RequiredPerFinishedUnit 的分母固定是「最終成品一個單位」，不是直接上階父件，
/// 因此中間階用量會被逐層累乘（見 docs/ai-assistant-module-plan-v2.md 第 3.1 節）。
public sealed record ExplodedComponent(
    string ComponentCode,
    string ComponentName,
    decimal RequiredPerFinishedUnit);

public sealed record ShortageComponent(
    string ComponentCode,
    string ComponentName,
    decimal RequiredPerFinishedUnit,
    decimal AvailableQty,
    decimal ShortfallQty);

public sealed record MaterialSufficiencyResult(
    string ItemCode,
    string BomVersion,
    string Basis,
    decimal MaxBuildableQty,
    bool? SufficientForRequestedQty,
    IReadOnlyList<ShortageComponent> ShortageComponents);
