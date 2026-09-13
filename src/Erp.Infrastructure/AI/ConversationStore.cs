namespace Erp.Infrastructure.AI;

/// 一輪完整的問答（使用者問什麼、助理最後回了什麼）。
///
/// 刻意不存工具呼叫與工具結果：
/// 1. Anthropic 的訊息格式要求 tool_use 與 tool_result 成對出現且緊鄰，
///    歷史裡塞半套會變成格式錯誤，而那種錯誤只有真的打 API 才會現形。
/// 2. 工具結果整包很肥，同一段 JSON 每輪重送一次是純粹的 token 浪費。
/// 3. 助理的回答本身就被要求引用工具回傳的數字，關鍵數字已經在文字裡。
///
/// 代價要說清楚：LLM 看不到上一輪工具輸出的完整內容，追問細節時它會再呼叫一次工具 ——
/// 這反而保證了數字是當下查的，不是從歷史裡抄的。
public sealed record ConversationTurn(string Question, string Answer);

/// 跨請求的對話記憶。
public interface IConversationStore
{
    /// 取得這個對話最近的幾輪；id 不存在（或已過期、被淘汰）時回空清單
    IReadOnlyList<ConversationTurn> GetRecentTurns(string conversationId);

    void Append(string conversationId, ConversationTurn turn);
}

/// 記憶體版對話記憶。
///
/// 為什麼不落地到 SQLite：對話上下文是短暫的，做成資料表就得回答
/// 「誰來清、保留多久、要不要備份」這一整串與這個模組無關的問題。
/// 代價是重啟後對話歷史消失 —— 對這個規模的系統是可以接受的取捨，
/// 而且介面留在這裡，要換成持久化只需要換一個實作。
///
/// 兩個上限都是必要的，不是防禦性裝飾：這是個長時間執行的服務，
/// 沒有上限的話每一個新對話都會永久佔著記憶體，直到行程重啟為止。
public sealed class InMemoryConversationStore(
    int maxTurnsPerConversation,
    int maxConversations,
    TimeSpan idleTimeout,
    TimeProvider? timeProvider = null) : IConversationStore
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Conversation> _conversations = [];

    public IReadOnlyList<ConversationTurn> GetRecentTurns(string conversationId)
    {
        lock (_gate)
        {
            PurgeExpired();

            if (!_conversations.TryGetValue(conversationId, out var conversation))
            {
                return [];
            }

            conversation.LastAccessed = _time.GetUtcNow();
            return [.. conversation.Turns];
        }
    }

    public void Append(string conversationId, ConversationTurn turn)
    {
        lock (_gate)
        {
            PurgeExpired();

            if (!_conversations.TryGetValue(conversationId, out var conversation))
            {
                EvictOldestIfFull();
                conversation = new Conversation();
                _conversations[conversationId] = conversation;
            }

            conversation.Turns.Add(turn);
            conversation.LastAccessed = _time.GetUtcNow();

            // 只留最近幾輪。超出的從最舊的丟 —— 追問指的幾乎都是剛剛講過的事。
            while (conversation.Turns.Count > maxTurnsPerConversation)
            {
                conversation.Turns.RemoveAt(0);
            }
        }
    }

    private void PurgeExpired()
    {
        var cutoff = _time.GetUtcNow() - idleTimeout;

        var expired = _conversations
            .Where(kvp => kvp.Value.LastAccessed < cutoff)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in expired)
        {
            _conversations.Remove(key);
        }
    }

    /// 淘汰最久沒被碰過的那個對話。過期清理已經先跑過，
    /// 還是滿的話代表真的有這麼多活躍對話，只能挑一個犧牲。
    private void EvictOldestIfFull()
    {
        if (_conversations.Count < maxConversations)
        {
            return;
        }

        var oldest = _conversations.MinBy(kvp => kvp.Value.LastAccessed).Key;
        _conversations.Remove(oldest);
    }

    private sealed class Conversation
    {
        public List<ConversationTurn> Turns { get; } = [];
        public DateTimeOffset LastAccessed { get; set; }
    }
}
