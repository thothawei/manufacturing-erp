namespace Erp.Application.Common;

/// 可行性計算的庫存基準。
/// 目前全系統固定使用 Available（扣除已保留量），回傳給 AI 工具時一併帶出，
/// 讓 LLM 能向使用者說明前提，避免「系統說夠、現場缺料」的誤解。
public static class CalculationBasis
{
    public const string Available = "available";
}
