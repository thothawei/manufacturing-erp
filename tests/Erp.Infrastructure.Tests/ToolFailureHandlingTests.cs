using System.Text.Json;
using Erp.Application.Bom;
using Erp.Application.Common;
using Erp.Application.Inventory;
using Erp.Application.Items;
using Erp.Application.Mrp;
using Erp.Application.Production;
using Erp.Application.Purchasing;
using Erp.Application.Quality;
using Erp.Infrastructure.AI;
using Erp.Infrastructure.Persistence;
using Erp.Infrastructure.Persistence.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Erp.Infrastructure.Tests;

/// 未預期例外的處理。
///
/// 沒有這層攔截時，資料庫連線失效這類例外會穿過 ToolDispatcher、穿過 tool-use 迴圈，
/// 一路變成 HTTP 500 —— 即使其他工具的結果其實是好的，整段對話也一起陣亡。
public class ToolFailureHandlingTests
{
    private static readonly DateOnly Today = new(2026, 9, 10);

    /// 建一個資料庫已經失效的 dispatcher：查詢一定會拋出未預期的例外
    private static async Task<(ToolDispatcher Dispatcher, CapturingLogger<ToolDispatcher> Logger)>
        CreateBrokenDispatcherAsync()
    {
        var fixture = new SqliteTestDatabase();
        var clock = new TestClock(Today);
        await ErpDbSeeder.SeedAsync(fixture.Db, clock);

        var logger = new CapturingLogger<ToolDispatcher>();
        var dispatcher = TestServices.CreateDispatcher(fixture, clock, logger);

        // 關掉 in-memory SQLite 連線，之後任何查詢都會炸
        await fixture.DisposeAsync();

        return (dispatcher, logger);
    }

    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public async Task 資料庫失效時回報錯誤而不是把例外往上拋()
    {
        var (dispatcher, _) = await CreateBrokenDispatcherAsync();

        var result = await dispatcher.ExecuteAsync(ToolCatalog.SearchItems, Args(new { keyword = "面板" }));

        Assert.True(result.IsError);
        Assert.Contains("INTERNAL_ERROR", result.Content);
        Assert.Contains("系統錯誤", result.Content);
    }

    [Fact]
    public async Task 回給LLM的訊息不含例外型別_堆疊或任何內部細節()
    {
        var (dispatcher, _) = await CreateBrokenDispatcherAsync();

        var result = await dispatcher.ExecuteAsync(
            ToolCatalog.GetItemInventoryStatus, Args(new { item_code = "PANEL-01" }));

        // 這些字串若出現在 tool_result 裡，LLM 有可能原樣轉述給使用者
        Assert.DoesNotContain("Exception", result.Content);
        Assert.DoesNotContain("Sqlite", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("   at ", result.Content);
        Assert.DoesNotContain("Erp.Infrastructure", result.Content);
    }

    [Fact]
    public async Task 例外全文會寫進伺服器log()
    {
        var (dispatcher, logger) = await CreateBrokenDispatcherAsync();

        await dispatcher.ExecuteAsync(ToolCatalog.SearchItems, Args(new { keyword = "面板" }));

        // 現在每次呼叫還會多一筆 Information 的稽核紀錄，這裡只看錯誤那筆
        var entry = Assert.Single(logger.Entries, e => e.Level == LogLevel.Error);
        Assert.NotNull(entry.Exception);                      // 例外物件本身要留下來
        Assert.Contains(ToolCatalog.SearchItems, entry.Message);
        Assert.Contains("面板", entry.Message);                // 參數也要記，才追得出是什麼查詢炸的
    }

    [Fact]
    public async Task 每個工具的未預期例外都被攔下()
    {
        // 只補在某幾個 case 沒有意義，這裡把目錄裡每個工具都走一遍
        foreach (var tool in ToolCatalog.All)
        {
            var (dispatcher, _) = await CreateBrokenDispatcherAsync();

            var arguments = Args(tool.Required.ToDictionary(name => name, object (_) => "X"));
            var result = await dispatcher.ExecuteAsync(tool.Name, arguments);

            Assert.True(result.IsError, $"工具 {tool.Name} 在資料庫失效時沒有回報錯誤");
            Assert.DoesNotContain("Exception", result.Content);
        }
    }

    [Fact]
    public async Task 呼叫端主動取消時例外要往上拋而不是被當成工具失敗()
    {
        // 使用者關掉連線或請求逾時屬於「這次對話不用做了」，
        // 不該被降級成一個 tool_result 讓迴圈繼續跑下去
        var fixture = new SqliteTestDatabase();
        var clock = new TestClock(Today);
        await ErpDbSeeder.SeedAsync(fixture.Db, clock);
        var dispatcher = TestServices.CreateDispatcher(fixture, clock);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            dispatcher.ExecuteAsync(ToolCatalog.SearchItems, Args(new { keyword = "面板" }), cts.Token));

        await fixture.DisposeAsync();
    }

    [Fact]
    public async Task 一個工具炸掉時_同一輪其他工具的結果仍然送達LLM()
    {
        // 這是 G1 真正的價值：整段對話不會因為一個工具失敗就全毀
        var fixture = new SqliteTestDatabase();
        var clock = new TestClock(Today);
        await ErpDbSeeder.SeedAsync(fixture.Db, clock);
        var dispatcher = TestServices.CreateDispatcher(fixture, clock);

        var llm = new FakeLlmClient(
            FakeLlmClient.ToolUses(
                ("ok", ToolCatalog.GetItemInventoryStatus, new { item_code = "PANEL-01" }),
                ("bad", ToolCatalog.GetWorkOrderProgress, new { work_order_no = "NOT-EXIST" })),
            FakeLlmClient.Text("面板可用 80 片；那張工單查不到。"));

        var service = new AiAssistantService(
            llm, dispatcher, Options.Create(new AiAssistantOptions()),
            NullLogger<AiAssistantService>.Instance);

        var answer = await service.AskAsync("面板庫存跟 NOT-EXIST 的進度");

        var results = llm.ReceivedRequests[1].Messages[^1].Content.Cast<LlmToolResultBlock>().ToList();
        Assert.Equal(2, results.Count);

        var ok = results.Single(r => r.ToolUseId == "ok");
        Assert.False(ok.IsError);
        Assert.Contains("\"available_qty\":80", ok.Content);   // 好的那個結果完整送達

        var bad = results.Single(r => r.ToolUseId == "bad");
        Assert.True(bad.IsError);

        Assert.Contains("80 片", answer);
        await fixture.DisposeAsync();
    }
}
