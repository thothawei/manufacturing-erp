using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Erp.Api.Tests;

/// 人工確認流程走真的 HTTP。
///
/// 這幾個端點刻意**不是** AI 工具：AI 產生的是待確認的建議，
/// 把建議變成正式採購單只有這裡做得到。
public class PurchaseSuggestionEndpointTests(ErpApiFactory factory) : IClassFixture<ErpApiFactory>
{
    private HttpClient Client => factory.CreateClient();

    [Fact]
    public async Task 清單端點在沒有建議時回空陣列()
    {
        var response = await Client.GetAsync("/api/purchase-suggestions?status=PendingApproval");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, body.ValueKind);
    }

    [Fact]
    public async Task 核准一個不存在的建議回404()
    {
        var response = await Client.PostAsJsonAsync(
            "/api/purchase-suggestions/PS-NOT-EXIST/approve", new { decidedBy = "王採購" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task 沒有指明決定者回400()
    {
        var response = await Client.PostAsJsonAsync(
            "/api/purchase-suggestions/PS-ANY/approve", new { decidedBy = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task 駁回一個不存在的建議回404()
    {
        var response = await Client.PostAsJsonAsync(
            "/api/purchase-suggestions/PS-NOT-EXIST/reject", new { decidedBy = "李採購" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
