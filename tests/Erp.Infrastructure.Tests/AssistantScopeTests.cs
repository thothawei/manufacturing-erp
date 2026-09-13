using System.Text.Json;
using Erp.Infrastructure.AI;
using Erp.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Erp.Infrastructure.Tests;

/// 角色範圍隔離。
///
/// 驗的是兩道防線都在：送給 LLM 的工具清單有過濾，**而且**執行時也擋。
/// 只做前者是不夠的 —— 工具名稱是模型生成的字串，它可以叫出一個從沒出現在
/// 清單裡的名字，那時只剩執行時這道擋得住。
///
/// 這一層是工具層級的邊界，不是資料列層級的隔離：允許的工具仍然查得到全庫資料。
/// 測試不假裝驗了後者。
public class AssistantScopeTests : IAsyncLifetime
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

    private AiAssistantService CreateService(FakeLlmClient llm, IConversationStore? store = null)
        => new(llm, _dispatcher, store ?? TestServices.CreateConversationStore(),
            Options.Create(new AiAssistantOptions()),
            NullLogger<AiAssistantService>.Instance);

    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    // ── 第一道：送給 LLM 的工具清單 ──

    [Fact]
    public void 不指定角色時十個工具全開()
    {
        Assert.Equal(ToolCatalog.All.Count, AssistantScope.ToolsFor(null).Count);
        Assert.Equal(ToolCatalog.All.Count, AssistantScope.ToolsFor("").Count);
    }

    [Theory]
    [InlineData(AssistantScope.Production, ToolCatalog.ListOpenPurchaseOrders)]
    [InlineData(AssistantScope.Purchasing, ToolCatalog.GetQualityInspectionSummary)]
    [InlineData(AssistantScope.Purchasing, ToolCatalog.GetWorkOrderProgress)]
    [InlineData(AssistantScope.Quality, ToolCatalog.RunMrpShortageAnalysis)]
    [InlineData(AssistantScope.Quality, ToolCatalog.ListOpenPurchaseOrders)]
    public void 被限制的工具不會出現在送給LLM的清單裡(string role, string forbiddenTool)
    {
        var names = AssistantScope.ToolsFor(role).Select(t => t.Name);

        Assert.DoesNotContain(forbiddenTool, names);
    }

    [Theory]
    [InlineData(AssistantScope.Production)]
    [InlineData(AssistantScope.Purchasing)]
    [InlineData(AssistantScope.Quality)]
    public void 每個角色都拿得到基本查詢與文件檢索(string role)
    {
        var names = AssistantScope.ToolsFor(role).Select(t => t.Name).ToList();

        Assert.Contains(ToolCatalog.SearchItems, names);
        Assert.Contains(ToolCatalog.GetItemInventoryStatus, names);
        Assert.Contains(ToolCatalog.SearchDocuments, names);
    }

    [Fact]
    public void 每個角色都少了至少一個工具()
    {
        // 如果哪個角色其實等於全開，這一層就沒有在隔離任何東西
        foreach (var role in AssistantScope.KnownRoles)
        {
            Assert.True(AssistantScope.ToolsFor(role).Count < ToolCatalog.All.Count,
                $"角色 {role} 拿得到全部工具，這個角色沒有任何邊界");
        }
    }

    [Fact]
    public void 角色清單裡的工具名稱都真的存在()
    {
        // 工具改名時，這裡的對應表會靜默漏掉它 —— 那個工具會變成所有角色都不能用
        var known = ToolCatalog.All.Select(t => t.Name).ToHashSet();

        foreach (var role in AssistantScope.KnownRoles)
        {
            Assert.All(AssistantScope.ToolsFor(role), t => Assert.Contains(t.Name, known));
        }
    }

    [Fact]
    public void 每個工具至少有一個角色用得到()
    {
        // 反過來的漏洞：新增工具卻忘了加進任何角色，它就只有「不指定角色」時能用
        foreach (var tool in ToolCatalog.All)
        {
            Assert.True(
                AssistantScope.KnownRoles.Any(r => AssistantScope.IsAllowed(r, tool.Name)),
                $"工具 {tool.Name} 沒有被指派給任何角色，新增工具時要一併更新 AssistantScope");
        }
    }

    // ── 第二道：執行時 ──

    [Fact]
    public async Task 直接呼叫未授權的工具會被擋下來而不是執行()
    {
        var result = await _dispatcher.ExecuteAsync(
            ToolCatalog.ListOpenPurchaseOrders, Args(new { }), AssistantScope.Quality);

        Assert.True(result.IsError);
        Assert.Contains("NOT_AUTHORIZED", result.Content);

        // 確認真的沒有把資料查出來
        Assert.DoesNotContain("SUP-008", result.Content);
    }

    [Fact]
    public async Task 同一個工具換個有權限的角色就查得到()
    {
        var result = await _dispatcher.ExecuteAsync(
            ToolCatalog.ListOpenPurchaseOrders, Args(new { }), AssistantScope.Purchasing);

        Assert.False(result.IsError);
        Assert.Contains("SUP-008", result.Content);
    }

    [Fact]
    public async Task LLM硬要呼叫清單外的工具時執行層仍然擋得住()
    {
        // 這正是「只過濾清單」不夠的那個情境：工具名是模型生成的字串，
        // 它可以叫出一個從來沒出現在自己清單裡的名字
        var llm = new FakeLlmClient(
            FakeLlmClient.ToolUse("t1", ToolCatalog.ListOpenPurchaseOrders, new { }),
            FakeLlmClient.Text("我沒有查詢採購單的權限。"));

        await CreateService(llm).AskAsync("採購單狀況？", role: AssistantScope.Quality);

        var toolResult = (LlmToolResultBlock)llm.ReceivedRequests[1].Messages[^1].Content[0];

        Assert.True(toolResult.IsError);
        Assert.Contains("NOT_AUTHORIZED", toolResult.Content);
        Assert.DoesNotContain("SUP-008", toolResult.Content);
    }

    [Fact]
    public async Task 送給LLM的請求裡只有這個角色的工具()
    {
        var llm = new FakeLlmClient(FakeLlmClient.Text("好的。"));

        await CreateService(llm).AskAsync("有什麼可以查？", role: AssistantScope.Quality);

        var toolNames = llm.ReceivedRequests[0].Tools.Select(t => t.Name).ToList();

        Assert.DoesNotContain(ToolCatalog.ListOpenPurchaseOrders, toolNames);
        Assert.DoesNotContain(ToolCatalog.RunMrpShortageAnalysis, toolNames);
        Assert.Contains(ToolCatalog.GetQualityInspectionSummary, toolNames);
    }

    // ── 未知角色與預設行為 ──

    [Theory]
    [InlineData("purchase")]      // 打錯字
    [InlineData("admin")]
    [InlineData("生管")]
    public async Task 不認得的角色要報錯而不是預設放行(string role)
    {
        // 靜默變成「全部工具都開」比直接報錯危險得多
        var llm = new FakeLlmClient(FakeLlmClient.Text("不會走到這裡"));

        await Assert.ThrowsAsync<ArgumentException>(
            () => CreateService(llm).AskAsync("問題", role: role));

        Assert.Empty(llm.ReceivedRequests);
    }

    [Fact]
    public async Task 不指定角色時行為與加這一層之前完全一樣()
    {
        var llm = new FakeLlmClient(
            FakeLlmClient.ToolUse("t1", ToolCatalog.ListOpenPurchaseOrders, new { }),
            FakeLlmClient.Text("採購單兩張。"));

        await CreateService(llm).AskAsync("採購單狀況？");

        Assert.Equal(ToolCatalog.All.Count, llm.ReceivedRequests[0].Tools.Count);

        var toolResult = (LlmToolResultBlock)llm.ReceivedRequests[1].Messages[^1].Content[0];
        Assert.False(toolResult.IsError);
    }

    // ── 對話記憶不得成為繞道 ──

    [Fact]
    public async Task 換個角色帶同一個識別碼_讀不到另一個角色的對話歷史()
    {
        // 工具過濾擋住的東西，不能從歷史繞回來
        var store = TestServices.CreateConversationStore();

        var first = new FakeLlmClient(FakeLlmClient.Text("品管不良率 3%，工單 WO-01。"));
        var answer = await CreateService(first, store)
            .AskAsync("品管結果如何？", role: AssistantScope.Quality);

        var second = new FakeLlmClient(FakeLlmClient.Text("好的。"));
        await CreateService(second, store)
            .AskAsync("剛剛那個數字再說一次", answer.ConversationId, AssistantScope.Purchasing);

        var texts = second.ReceivedRequests[0].Messages
            .SelectMany(m => m.Content.OfType<LlmTextBlock>().Select(b => b.Text))
            .ToList();

        Assert.DoesNotContain("品管結果如何？", texts);
        Assert.DoesNotContain("品管不良率 3%，工單 WO-01。", texts);
        Assert.Equal(["剛剛那個數字再說一次"], texts);
    }

    [Fact]
    public async Task 同一個角色帶同一個識別碼_歷史仍然接得起來()
    {
        // 上一條不能是靠「歷史根本沒生效」而通過的
        var store = TestServices.CreateConversationStore();

        var first = new FakeLlmClient(FakeLlmClient.Text("不良率 3%。"));
        var answer = await CreateService(first, store)
            .AskAsync("品管結果如何？", role: AssistantScope.Quality);

        var second = new FakeLlmClient(FakeLlmClient.Text("好的。"));
        await CreateService(second, store)
            .AskAsync("再說一次", answer.ConversationId, AssistantScope.Quality);

        var texts = second.ReceivedRequests[0].Messages
            .SelectMany(m => m.Content.OfType<LlmTextBlock>().Select(b => b.Text))
            .ToList();

        Assert.Contains("品管結果如何？", texts);
        Assert.Contains("不良率 3%。", texts);
    }
}
