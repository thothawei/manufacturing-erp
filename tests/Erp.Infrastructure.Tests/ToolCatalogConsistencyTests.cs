using System.Text.Json;
using Erp.Application.Inventory;
using Erp.Application.Items;
using Erp.Infrastructure.AI;
using Erp.Infrastructure.Persistence;
using Erp.Infrastructure.Persistence.Repositories;

namespace Erp.Infrastructure.Tests;

/// ToolCatalog 的 JSON Schema 是手寫的，ToolDispatcher 用字串比對參數名。
/// 兩邊漂掉時 C# 編譯不會失敗，只有實際執行到那個工具才會炸 ——
/// 這組測試就是用來把這種靜默漂移變成 CI 會紅的失敗。
/// （見 docs/ai-assistant-module-plan-v2.md 第 4 節第 5 點）
public class ToolCatalogConsistencyTests : IAsyncLifetime
{
    private SqliteTestDatabase _fixture = null!;
    private ToolDispatcher _dispatcher = null!;

    public async Task InitializeAsync()
    {
        _fixture = new SqliteTestDatabase();
        var clock = new TestClock(new DateOnly(2026, 9, 10));
        await ErpDbSeeder.SeedAsync(_fixture.Db, clock);

        var db = _fixture.CreateContext();
        var itemRepo = new ItemRepository(db);
        _dispatcher = new ToolDispatcher(
            new ItemMasterQueryService(itemRepo),
            new InventoryQueryService(itemRepo, new InventoryRepository(db), clock));
    }

    public async Task DisposeAsync() => await _fixture.DisposeAsync();

    /// 每個工具都要有對應的實作，且必填參數名要跟 dispatcher 讀取的名稱一致。
    /// 用種子資料中真實存在的值當引數，任何「未知工具」或「缺少必要參數」都代表漂移。
    [Theory]
    [MemberData(nameof(ToolNames))]
    public async Task 目錄裡的每個工具都接得上實作(string toolName)
    {
        var tool = ToolCatalog.All.Single(t => t.Name == toolName);
        var arguments = BuildValidArguments(tool);

        var result = await _dispatcher.ExecuteAsync(tool.Name, arguments);

        Assert.False(result.IsError,
            $"工具 {tool.Name} 用目錄宣告的參數呼叫失敗：{result.Content}");
    }

    [Fact]
    public async Task 每個必填參數缺席時都會被擋下()
    {
        // 反過來確認必填宣告不是裝飾用的：少一個參數就該報錯
        foreach (var tool in ToolCatalog.All)
        {
            foreach (var missing in tool.Required)
            {
                var arguments = BuildValidArguments(tool, omit: missing);
                var result = await _dispatcher.ExecuteAsync(tool.Name, arguments);

                Assert.True(result.IsError,
                    $"工具 {tool.Name} 缺少必填參數 {missing} 卻沒有回報錯誤");
                Assert.Contains(missing, result.Content);
            }
        }
    }

    [Fact]
    public void 工具名稱不重複且說明不為空()
    {
        Assert.Equal(ToolCatalog.All.Count, ToolCatalog.All.Select(t => t.Name).Distinct().Count());
        Assert.All(ToolCatalog.All, t => Assert.False(string.IsNullOrWhiteSpace(t.Description)));
    }

    [Fact]
    public void 必填參數都要出現在Schema的屬性裡()
    {
        foreach (var tool in ToolCatalog.All)
        {
            foreach (var required in tool.Required)
            {
                Assert.True(tool.Properties.ContainsKey(required),
                    $"工具 {tool.Name} 宣告 {required} 為必填，但 Schema 屬性裡沒有它");
            }
        }
    }

    public static TheoryData<string> ToolNames()
    {
        var data = new TheoryData<string>();
        foreach (var tool in ToolCatalog.All)
        {
            data.Add(tool.Name);
        }
        return data;
    }

    /// 用種子資料中真實存在的值填滿參數，讓失敗只可能來自「接不上」而非「查無資料」
    private static JsonElement BuildValidArguments(ToolDefinition tool, string? omit = null)
    {
        var values = new Dictionary<string, object>();

        foreach (var name in tool.Required.Where(n => n != omit))
        {
            values[name] = name switch
            {
                "keyword" => "PANEL",
                "item_code" => "PANEL-01",
                _ => throw new InvalidOperationException(
                    $"測試沒有為參數 {name} 準備範例值，新增工具時要一併補上")
            };
        }

        return JsonSerializer.SerializeToElement(values);
    }
}
