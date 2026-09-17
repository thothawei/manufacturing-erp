using Erp.Application.Ml;

namespace Erp.Infrastructure.Ml;

/// 記憶體版的最近推論記錄，一個有界的環狀緩衝區。
///
/// 上限是必要的，不是防禦性裝飾：這是個長時間執行的服務，沒有上限的話
/// 記憶體會隨請求數線性成長到行程重啟為止，理由跟 InMemoryConversationStore 一樣。
public sealed class InMemoryRecentPredictionLog<TFeatures>(int capacity) : IRecentPredictionLog<TFeatures>
{
    private readonly Lock _gate = new();
    private readonly Queue<TFeatures> _recent = new();

    public void Record(TFeatures features)
    {
        lock (_gate)
        {
            _recent.Enqueue(features);

            while (_recent.Count > capacity)
            {
                _recent.Dequeue();
            }
        }
    }

    public IReadOnlyList<TFeatures> GetRecent(int maxCount)
    {
        lock (_gate)
        {
            return [.. _recent.TakeLast(Math.Min(maxCount, _recent.Count))];
        }
    }
}
