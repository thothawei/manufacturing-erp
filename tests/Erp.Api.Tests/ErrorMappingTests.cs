using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Erp.Api.Tests;

/// 例外 → HTTP 狀態碼的對映。
///
/// 這層原本不存在：查無料號會回 500，而且回應體直接吐出完整堆疊與本機絕對路徑。
/// 語意也是錯的 —— 查不到資料是 404 不是 500。
public class ErrorMappingTests(ErpApiFactory factory) : IClassFixture<ErpApiFactory>
{
    private HttpClient Client => factory.CreateClient();

    [Fact]
    public async Task 查無料件回404()
    {
        var response = await Client.GetAsync("/api/items/NOT-EXIST/inventory");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("查無資料", problem.GetProperty("title").GetString());
        Assert.Contains("NOT-EXIST", problem.GetProperty("detail").GetString()!);
    }

    [Fact]
    public async Task 查無工單回404()
    {
        var response = await Client.GetAsync("/api/work-orders/NOPE/progress");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task 對原物料問可製造量回409而不是404()
    {
        // PANEL-01 存在，只是沒有 BOM。這跟「查無此料號」是不同的情況
        var response = await Client.GetAsync("/api/items/PANEL-01/sufficiency");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("沒有 BOM", problem.GetProperty("detail").GetString()!);
    }

    [Fact]
    public async Task 參數超出範圍回400()
    {
        var response = await Client.GetAsync("/api/mrp/shortages?planningHorizonDays=0");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AI服務無法連線回503()
    {
        var response = await Client.PostAsJsonAsync("/api/ai-assistant/ask", new { question = "測試" });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("AI 助理暫時無法使用", problem.GetProperty("title").GetString());
    }

    [Fact]
    public async Task 空白問題回400()
    {
        var response = await Client.PostAsJsonAsync("/api/ai-assistant/ask", new { question = "   " });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/items/NOT-EXIST/inventory")]
    [InlineData("/api/items/PANEL-01/sufficiency")]
    [InlineData("/api/work-orders/NOPE/progress")]
    [InlineData("/api/mrp/shortages?planningHorizonDays=0")]
    public async Task 錯誤回應不得含堆疊或本機路徑(string url)
    {
        var body = await (await Client.GetAsync(url)).Content.ReadAsStringAsync();

        Assert.DoesNotContain("   at Erp.", body);
        Assert.DoesNotContain("/Users/", body);
        Assert.DoesNotContain("Exception:", body);
    }
}

public class HappyPathTests(ErpApiFactory factory) : IClassFixture<ErpApiFactory>
{
    private HttpClient Client => factory.CreateClient();

    [Fact]
    public async Task 健康檢查回200()
    {
        var response = await Client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task 種子資料在啟動時灌入_可查到可用庫存()
    {
        var response = await Client.GetAsync("/api/items/PANEL-01/inventory");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stock = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(100, stock.GetProperty("onHandQty").GetInt32());
        Assert.Equal(80, stock.GetProperty("availableQty").GetInt32());
    }

    [Fact]
    public async Task 可製造量端點回傳後端算好的數字()
    {
        var response = await Client.GetAsync("/api/items/TV-100/sufficiency");
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(40, result.GetProperty("maxBuildableQty").GetInt32());
        Assert.Equal("available", result.GetProperty("basis").GetString());
    }
}
