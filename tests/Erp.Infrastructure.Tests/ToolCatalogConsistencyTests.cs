using System.Text.Json;
using Erp.Infrastructure.AI;
using Erp.Infrastructure.Persistence;

namespace Erp.Infrastructure.Tests;

/// ToolCatalog 的 JSON Schema 是手寫的，ToolDispatcher 用字串比對參數名。
/// 兩邊漂掉時 C# 編譯不會失敗，只有實際執行到那個工具才會炸 ——
/// 這組測試就是用來把這種靜默漂移變成 CI 會紅的失敗。
/// （見 docs/ai-assistant-module-plan-v2.md 第 4 節第 5 點）
public class ToolCatalogConsistencyTests : IAsyncLifetime
{
    private static readonly DateOnly Today = new(2026, 9, 10);
    private static readonly string Stamp = Today.ToString("yyyyMMdd");

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

    /// 每個工具都要有對應的實作，且必填參數名要跟 dispatcher 讀取的名稱一致。
    /// 用種子資料中真實存在的值當引數，任何「未知工具」或「缺少必要參數」都代表漂移。
    [Theory]
    [MemberData(nameof(ToolNames))]
    public async Task 只帶必填參數時每個工具都接得上實作(string toolName)
    {
        var tool = ToolCatalog.All.Single(t => t.Name == toolName);

        var result = await _dispatcher.ExecuteAsync(tool.Name, BuildArguments(tool));

        Assert.False(result.IsError,
            $"工具 {tool.Name} 用目錄宣告的必填參數呼叫失敗：{result.Content}");
    }

    /// 選填參數不在 Required 裡，只測必填的話它們改名了也不會被發現。
    /// 這條把目錄宣告的每個參數都真的帶進去跑一次。
    [Theory]
    [MemberData(nameof(ToolNames))]
    public async Task 帶上全部宣告參數時每個工具都接得上實作(string toolName)
    {
        var tool = ToolCatalog.All.Single(t => t.Name == toolName);

        var result = await _dispatcher.ExecuteAsync(tool.Name, BuildArguments(tool, includeOptional: true));

        Assert.False(result.IsError,
            $"工具 {tool.Name} 帶上全部宣告參數呼叫失敗：{result.Content}");
    }

    [Fact]
    public async Task 每個必填參數缺席時都會被擋下()
    {
        // 反過來確認必填宣告不是裝飾用的：少一個參數就該報錯
        foreach (var tool in ToolCatalog.All)
        {
            foreach (var missing in tool.Required)
            {
                var arguments = BuildArguments(tool, omit: missing);
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
    private static JsonElement BuildArguments(
        ToolDefinition tool, bool includeOptional = false, string? omit = null)
    {
        var names = includeOptional ? tool.Properties.Keys.AsEnumerable() : tool.Required;

        var values = names
            .Where(n => n != omit)
            .ToDictionary(n => n, n => SampleValue(tool.Name, n));

        return JsonSerializer.SerializeToElement(values);
    }

    /// 範例值要對得上種子資料，而且要看工具是誰 ——
    /// 例如 PANEL-01 是原物料沒有 BOM，拿去問可製造量本來就該失敗。
    private static object SampleValue(string toolName, string parameterName) =>
        (toolName, parameterName) switch
        {
            (ToolCatalog.CheckMaterialSufficiency, "item_code") => "TV-100",
            (ToolCatalog.CheckMaterialSufficiency, "planned_qty") => 10,

            (_, "keyword") => "PANEL",
            (_, "item_code") => "PANEL-01",
            (_, "work_order_no") => $"WO-{Stamp}-01",
            (_, "supplier_code") => "SUP-008",
            (_, "planning_horizon_days") => 30,
            (_, "date_range_start") => "2026-09-01",
            (_, "date_range_end") => "2026-09-30",

            _ => throw new InvalidOperationException(
                $"測試沒有為工具 {toolName} 的參數 {parameterName} 準備範例值，新增工具時要一併補上")
        };
}
