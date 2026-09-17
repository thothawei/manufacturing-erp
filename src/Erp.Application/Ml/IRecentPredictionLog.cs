namespace Erp.Application.Ml;

/// 最近一批推論請求的特徵，給 PSI 漂移偵測用（S5）。
///
/// 泛型而不是每個模型各寫一個型別：兩個模型（延遲風險、物料需求預測）需要的行為
/// 完全一樣——記錄、取最近幾筆——差別只在特徵型別，沒有理由寫兩次。
/// DI 針對每個 TFeatures 各自註冊一個 singleton 實例，兩個模型的記錄不會混在一起。
///
/// 記憶體版、沒有持久化：跟 IConversationStore 是同一個取捨，
/// 服務重啟後记录消失，對這個規模的系統是可以接受的代價。
public interface IRecentPredictionLog<TFeatures>
{
    void Record(TFeatures features);

    /// 最近至多 maxCount 筆，由舊到新排序；不足 maxCount 筆時回實際筆數
    IReadOnlyList<TFeatures> GetRecent(int maxCount);
}
