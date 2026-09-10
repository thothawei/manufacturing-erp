using System.Text.Json;
using Erp.Infrastructure.AI;
using Erp.Infrastructure.Persistence;

namespace Erp.Infrastructure.Tests;

/// 錯誤回傳的契約：每個失敗都要有機器可讀的 error_code 與給人看的 message。
///
/// 只回中文訊息的話，LLM 只能靠字串比對猜錯誤類型 ——
/// 而它該對「查無資料」「參數不對」「系統錯誤」做出不同反應。
public class ToolErrorContractTests : IAsyncLifetime
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

    private async Task<(string Code, string Message)> ExecuteExpectingErrorAsync(
        string toolName, object arguments)
    {
        var result = await _dispatcher.ExecuteAsync(
            toolName, JsonSerializer.SerializeToElement(arguments));

        Assert.True(result.IsError, $"預期失敗但成功了：{result.Content}");

        using var json = JsonDocument.Parse(result.Content);
        var root = json.RootElement;

        Assert.True(root.TryGetProperty("error_code", out var code), $"缺少 error_code：{result.Content}");
        Assert.True(root.TryGetProperty("message", out var message), $"缺少 message：{result.Content}");

        return (code.GetString()!, message.GetString()!);
    }

    [Fact]
    public async Task 查無資料回ENTITY_NOT_FOUND()
    {
        var (code, message) = await ExecuteExpectingErrorAsync(
            ToolCatalog.GetItemInventoryStatus, new { item_code = "NOT-EXIST" });

        Assert.Equal("ENTITY_NOT_FOUND", code);
        Assert.Contains("找不到料件", message);   // message 仍要說清楚是什麼查不到
    }

    [Fact]
    public async Task 工單查無資料同樣回ENTITY_NOT_FOUND()
    {
        var (code, message) = await ExecuteExpectingErrorAsync(
            ToolCatalog.GetWorkOrderProgress, new { work_order_no = "WO-NOT-EXIST" });

        Assert.Equal("ENTITY_NOT_FOUND", code);
        Assert.Contains("工單", message);
    }

    [Fact]
    public async Task 缺少必填參數回INVALID_ARGUMENT()
    {
        var (code, message) = await ExecuteExpectingErrorAsync(
            ToolCatalog.GetItemInventoryStatus, new { wrong_name = "PANEL-01" });

        Assert.Equal("INVALID_ARGUMENT", code);
        Assert.Contains("item_code", message);   // 要講出是哪個參數，LLM 才知道怎麼改
    }

    [Fact]
    public async Task 日期格式錯誤回INVALID_ARGUMENT()
    {
        var (code, message) = await ExecuteExpectingErrorAsync(
            ToolCatalog.ListWorkOrdersAtRisk, new { date_range_start = "上週一" });

        Assert.Equal("INVALID_ARGUMENT", code);
        Assert.Contains("YYYY-MM-DD", message);
    }

    [Fact]
    public async Task 數字格式錯誤回INVALID_ARGUMENT()
    {
        var (code, _) = await ExecuteExpectingErrorAsync(
            ToolCatalog.CheckMaterialSufficiency, new { item_code = "TV-100", planned_qty = "很多" });

        Assert.Equal("INVALID_ARGUMENT", code);
    }

    [Fact]
    public async Task 對原物料問可製造量回NOT_APPLICABLE而不是查無資料()
    {
        // PANEL-01 存在，只是沒有 BOM。這跟「查無此料號」是不同的情況，
        // LLM 該說的話也不一樣
        var (code, message) = await ExecuteExpectingErrorAsync(
            ToolCatalog.CheckMaterialSufficiency, new { item_code = "PANEL-01" });

        Assert.Equal("NOT_APPLICABLE", code);
        Assert.Contains("沒有 BOM", message);
    }

    [Fact]
    public async Task 未知工具回UNKNOWN_TOOL()
    {
        var (code, _) = await ExecuteExpectingErrorAsync("delete_everything", new { });

        Assert.Equal("UNKNOWN_TOOL", code);
    }

    [Fact]
    public async Task 每個錯誤碼都有對應的線路字串且不重複()
    {
        var codes = Enum.GetValues<ToolErrorCode>();
        var wireValues = codes.Select(c => c.ToWireValue()).ToList();

        Assert.Equal(codes.Length, wireValues.Distinct().Count());
        Assert.All(wireValues, v => Assert.Matches("^[A-Z_]+$", v));
    }

    [Fact]
    public async Task 成功的結果不會帶error_code()
    {
        var result = await _dispatcher.ExecuteAsync(
            ToolCatalog.GetItemInventoryStatus,
            JsonSerializer.SerializeToElement(new { item_code = "PANEL-01" }));

        Assert.False(result.IsError);
        using var json = JsonDocument.Parse(result.Content);
        Assert.False(json.RootElement.TryGetProperty("error_code", out _));
    }
}
