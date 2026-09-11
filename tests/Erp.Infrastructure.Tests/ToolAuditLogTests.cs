using System.Text.Json;
using Erp.Application.Bom;
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

namespace Erp.Infrastructure.Tests;

/// 工具呼叫的稽核紀錄。
///
/// AI 助理最後只吐出一段自然語言，沒有這條 log 就無從得知那段話是根據哪些查詢組出來的。
/// 這也是「整個過程可追蹤、不是黑箱」的依據。
public class ToolAuditLogTests : IAsyncLifetime
{
    private static readonly DateOnly Today = new(2026, 9, 10);

    private SqliteTestDatabase _fixture = null!;
    private ToolDispatcher _dispatcher = null!;
    private CapturingLogger<ToolDispatcher> _logger = null!;

    public async Task InitializeAsync()
    {
        _fixture = new SqliteTestDatabase();
        var clock = new TestClock(Today);
        await ErpDbSeeder.SeedAsync(_fixture.Db, clock);

        _logger = new CapturingLogger<ToolDispatcher>();
        _dispatcher = TestServices.CreateDispatcher(_fixture, clock, _logger);
    }

    public async Task DisposeAsync() => await _fixture.DisposeAsync();

    private Task<ToolExecutionResult> ExecuteAsync(string toolName, object arguments)
        => _dispatcher.ExecuteAsync(toolName, JsonSerializer.SerializeToElement(arguments));

    private LogEntry SingleAuditEntry()
        => Assert.Single(_logger.Entries, e => e.Level == LogLevel.Information);

    [Fact]
    public async Task 成功的呼叫會留下工具名稱_參數_耗時與成功狀態()
    {
        await ExecuteAsync(ToolCatalog.GetItemInventoryStatus, new { item_code = "PANEL-01" });

        var entry = SingleAuditEntry();
        Assert.Contains(ToolCatalog.GetItemInventoryStatus, entry.Message);
        Assert.Contains("PANEL-01", entry.Message);
        Assert.Contains("成功：True", entry.Message);
        Assert.Matches(@"耗時 \d+ ms", entry.Message);
    }

    [Fact]
    public async Task 已知失敗也會留下紀錄並標記為不成功()
    {
        await ExecuteAsync(ToolCatalog.GetItemInventoryStatus, new { item_code = "NOT-EXIST" });

        var entry = SingleAuditEntry();
        Assert.Contains("成功：False", entry.Message);
        Assert.Contains("NOT-EXIST", entry.Message);
    }

    [Fact]
    public async Task 參數裡的中文不會被逃逸成unicode()
    {
        // log 是給人看的，\uXXXX 讀不出來查了什麼
        await ExecuteAsync(ToolCatalog.SearchItems, new { keyword = "面板" });

        var entry = SingleAuditEntry();
        Assert.Contains("面板", entry.Message);
        Assert.DoesNotContain("\\u9762", entry.Message);
    }

    [Fact]
    public async Task 每次呼叫各留一筆_多次呼叫不會漏記()
    {
        await ExecuteAsync(ToolCatalog.SearchItems, new { keyword = "面板" });
        await ExecuteAsync(ToolCatalog.GetItemInventoryStatus, new { item_code = "PANEL-01" });
        await ExecuteAsync(ToolCatalog.ListOpenPurchaseOrders, new { });

        var audits = _logger.Entries.Where(e => e.Level == LogLevel.Information).ToList();
        Assert.Equal(3, audits.Count);
    }

    [Fact]
    public async Task 稽核紀錄不含金鑰或連線字串之類的機密()
    {
        await ExecuteAsync(ToolCatalog.SearchItems, new { keyword = "面板" });

        var entry = SingleAuditEntry();
        Assert.DoesNotContain("sk-ant", entry.Message);
        Assert.DoesNotContain("Data Source", entry.Message);
    }
}
