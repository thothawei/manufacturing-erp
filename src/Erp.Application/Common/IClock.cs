namespace Erp.Application.Common;

/// 讓「今天」可以在測試中固定，風險判定與 MRP 都依賴它
public interface IClock
{
    DateOnly Today { get; }
}
