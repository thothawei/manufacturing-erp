using Erp.Infrastructure.AI;
using Erp.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Erp.Infrastructure.Tests;

/// 跨請求的對話記憶。
///
/// 這裡驗的是「歷史有沒有被正確組進下一次請求」，不是「LLM 有沒有因此答對」——
/// 後者需要真實模型與行為評測，`AnthropicLiveApiTests` 才是那個層級。
/// 分清楚這件事很重要：用假 LLM 去斷言「它聽懂了追問」只會測到自己寫的腳本。
public class ConversationMemoryTests : IAsyncLifetime
{
    private static readonly DateOnly Today = new(2026, 9, 10);

    private SqliteTestDatabase _fixture = null!;
    private ToolDispatcher _dispatcher = null!;

    public async Task InitializeAsync()
    {
        _fixture = new SqliteTestDatabase();
        var clock = new TestClock(Today);
        await ErpDbSeeder.SeedAsync(_fixture.Db, clock);
        _dispatcher = TestServices.CreateDispatcher(_fixture, clock);
    }

    public async Task DisposeAsync() => await _fixture.DisposeAsync();

    private AiAssistantService CreateService(FakeLlmClient llm, IConversationStore store)
        => new(llm, _dispatcher, store,
            Options.Create(new AiAssistantOptions()),
            NullLogger<AiAssistantService>.Instance);

    /// 取出某次請求裡所有純文字訊息，攤平成 (角色, 文字)
    private static List<(LlmRole Role, string Text)> TextMessages(LlmRequest request)
        => [.. request.Messages
            .SelectMany(m => m.Content.OfType<LlmTextBlock>().Select(b => (m.Role, b.Text)))];

    [Fact]
    public async Task 第二次提問時會帶上前一輪的問答()
    {
        var store = TestServices.CreateConversationStore();

        var first = new FakeLlmClient(FakeLlmClient.Text("面板目前可用庫存 80 片。"));
        var answer = await CreateService(first, store).AskAsync("面板還有多少可以用？");

        var second = new FakeLlmClient(FakeLlmClient.Text("訊號線還有 500 條。"));
        await CreateService(second, store).AskAsync("那 CABLE-07 呢？", answer.ConversationId);

        var messages = TextMessages(second.ReceivedRequests[0]);

        Assert.Equal(
            [
                (LlmRole.User, "面板還有多少可以用？"),
                (LlmRole.Assistant, "面板目前可用庫存 80 片。"),
                (LlmRole.User, "那 CABLE-07 呢？")
            ],
            messages);
    }

    [Fact]
    public async Task 沒帶識別碼就是新對話_而且每次都拿到不同的識別碼()
    {
        var store = TestServices.CreateConversationStore();

        var first = new FakeLlmClient(FakeLlmClient.Text("答案一"));
        var a = await CreateService(first, store).AskAsync("問題一");

        var second = new FakeLlmClient(FakeLlmClient.Text("答案二"));
        var b = await CreateService(second, store).AskAsync("問題二");

        Assert.NotEqual(a.ConversationId, b.ConversationId);

        // 第二次沒帶 id，所以不該看得到第一輪
        var messages = TextMessages(second.ReceivedRequests[0]);
        Assert.Equal([(LlmRole.User, "問題二")], messages);
    }

    [Fact]
    public async Task 兩個不同的對話不會互相污染()
    {
        var store = TestServices.CreateConversationStore();

        var a1 = await CreateService(new FakeLlmClient(FakeLlmClient.Text("A 的答案")), store)
            .AskAsync("A 的問題");
        var b1 = await CreateService(new FakeLlmClient(FakeLlmClient.Text("B 的答案")), store)
            .AskAsync("B 的問題");

        var llm = new FakeLlmClient(FakeLlmClient.Text("A 的第二個答案"));
        await CreateService(llm, store).AskAsync("A 的追問", a1.ConversationId);

        var texts = TextMessages(llm.ReceivedRequests[0]).Select(m => m.Text).ToList();

        Assert.Contains("A 的問題", texts);
        Assert.DoesNotContain("B 的問題", texts);
        Assert.NotEqual(a1.ConversationId, b1.ConversationId);
    }

    [Fact]
    public async Task 帶一個不存在的識別碼會當成新對話而不是報錯()
    {
        var store = TestServices.CreateConversationStore();
        var unknown = Guid.NewGuid().ToString();

        var llm = new FakeLlmClient(FakeLlmClient.Text("答案"));
        var result = await CreateService(llm, store).AskAsync("問題", unknown);

        // 沿用傳進來的 id：使用者手上那個識別碼繼續有效，下一輪就接得起來
        Assert.Equal(unknown, result.ConversationId);
        Assert.Equal([(LlmRole.User, "問題")], TextMessages(llm.ReceivedRequests[0]));
    }

    [Fact]
    public async Task 超過上限的舊輪次會被丟掉_留下的是最近的幾輪()
    {
        // 輪數上限是 store 的職責，不是 service 的 —— 兩邊各管一份就會有兩個真相
        var store = TestServices.CreateConversationStore(maxTurns: 2);
        string? id = null;

        // 保留 2 輪，問 4 輪 → 只剩第 3、4 輪
        for (var i = 1; i <= 4; i++)
        {
            var llm = new FakeLlmClient(FakeLlmClient.Text($"答案 {i}"));
            id = (await CreateService(llm, store).AskAsync($"問題 {i}", id)).ConversationId;
        }

        var last = new FakeLlmClient(FakeLlmClient.Text("答案 5"));
        await CreateService(last, store).AskAsync("問題 5", id);

        var texts = TextMessages(last.ReceivedRequests[0]).Select(m => m.Text).ToList();

        Assert.DoesNotContain("問題 1", texts);
        Assert.DoesNotContain("問題 2", texts);
        Assert.Contains("問題 3", texts);
        Assert.Contains("問題 4", texts);
        Assert.Contains("問題 5", texts);
    }

    [Fact]
    public async Task 歷史不含工具呼叫與工具結果_只有問答文字()
    {
        var store = TestServices.CreateConversationStore();

        var first = new FakeLlmClient(
            FakeLlmClient.ToolUse("t1", ToolCatalog.GetItemInventoryStatus, new { item_code = "PANEL-01" }),
            FakeLlmClient.Text("面板可用 80 片。"));
        var answer = await CreateService(first, store).AskAsync("面板庫存？");

        var second = new FakeLlmClient(FakeLlmClient.Text("好的。"));
        await CreateService(second, store).AskAsync("謝謝", answer.ConversationId);

        var blocks = second.ReceivedRequests[0].Messages.SelectMany(m => m.Content).ToList();

        // tool_use／tool_result 必須成對且緊鄰，歷史裡塞半套會變成 API 格式錯誤
        Assert.DoesNotContain(blocks, b => b is LlmToolUseBlock);
        Assert.DoesNotContain(blocks, b => b is LlmToolResultBlock);
        Assert.Equal(3, blocks.Count);   // 舊問、舊答、新問
    }

    [Fact]
    public async Task System_prompt每一輪都還在()
    {
        var store = TestServices.CreateConversationStore();

        var first = new FakeLlmClient(FakeLlmClient.Text("答案一"));
        var answer = await CreateService(first, store).AskAsync("問題一");

        var second = new FakeLlmClient(FakeLlmClient.Text("答案二"));
        await CreateService(second, store).AskAsync("問題二", answer.ConversationId);

        Assert.Equal(AiSystemPrompt.Text, second.ReceivedRequests[0].SystemPrompt);
    }

    [Fact]
    public async Task 沒有結論的那一輪不寫進歷史()
    {
        // tool-use 迴圈撞到輪數上限時回的是一段說明，不是答案。
        // 留著它只會讓下一輪帶著一段沒有資訊的對白。
        var store = TestServices.CreateConversationStore();

        var looping = new FakeLlmClient
        {
            RepeatingResponse = FakeLlmClient.ToolUse(
                "t1", ToolCatalog.GetItemInventoryStatus, new { item_code = "PANEL-01" })
        };
        var stuck = new AiAssistantService(
            looping, _dispatcher, store,
            Options.Create(new AiAssistantOptions { MaxToolIterations = 2 }),
            NullLogger<AiAssistantService>.Instance);

        var result = await stuck.AskAsync("繞圈圈");

        var next = new FakeLlmClient(FakeLlmClient.Text("答案"));
        await CreateService(next, store).AskAsync("重新問", result.ConversationId);

        Assert.Equal([(LlmRole.User, "重新問")], TextMessages(next.ReceivedRequests[0]));
    }

    [Fact]
    public void 對話數達到上限時淘汰最久沒被碰過的那個()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 10, 9, 0, 0, TimeSpan.Zero));
        var store = new InMemoryConversationStore(
            maxTurnsPerConversation: 6, maxConversations: 2,
            idleTimeout: TimeSpan.FromMinutes(60), timeProvider: time);

        store.Append("a", new ConversationTurn("問 A", "答 A"));
        time.Advance(TimeSpan.FromMinutes(1));
        store.Append("b", new ConversationTurn("問 B", "答 B"));

        // 碰一下 a，讓 b 變成最久沒被使用的那個
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Single(store.GetRecentTurns("a"));

        time.Advance(TimeSpan.FromMinutes(1));
        store.Append("c", new ConversationTurn("問 C", "答 C"));

        Assert.Single(store.GetRecentTurns("a"));
        Assert.Empty(store.GetRecentTurns("b"));
        Assert.Single(store.GetRecentTurns("c"));
    }

    [Fact]
    public void 閒置超過期限的對話會被丟掉()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 10, 9, 0, 0, TimeSpan.Zero));
        var store = new InMemoryConversationStore(
            maxTurnsPerConversation: 6, maxConversations: 200,
            idleTimeout: TimeSpan.FromMinutes(30), timeProvider: time);

        store.Append("a", new ConversationTurn("問", "答"));

        time.Advance(TimeSpan.FromMinutes(29));
        Assert.Single(store.GetRecentTurns("a"));

        time.Advance(TimeSpan.FromMinutes(31));
        Assert.Empty(store.GetRecentTurns("a"));
    }
}

/// 測試用的可控時鐘。對話記憶的過期與淘汰都是時間相依的行為，
/// 用真實時間測等於不可能測（總不能讓測試睡 30 分鐘）。
internal sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan delta) => _now += delta;
}
