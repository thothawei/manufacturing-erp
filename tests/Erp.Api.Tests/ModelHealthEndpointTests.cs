using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Erp.Api.Tests;

/// /api/ml/model-health 走真的 HTTP，接的是 repo 裡真正的 ONNX 模型
/// （跟 DelayRiskModelTests 一樣不用假模型 —— 要驗的就是這個端點接得到真的模型狀態）。
public class ModelHealthEndpointTests(ErpApiFactory factory) : IClassFixture<ErpApiFactory>
{
    private HttpClient Client => factory.CreateClient();

    [Fact]
    public async Task 回傳模型狀態且特徵定義與程式碼一致()
    {
        var response = await Client.GetAsync("/api/ml/model-health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(body.GetProperty("available").GetBoolean(), body.ToString());
        // featureSchemaConsistent 為 false 時 available 必定也是 false —— 兩者不該互相矛盾
        Assert.True(body.GetProperty("featureSchemaConsistent").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("trainedOn").GetString()));
        Assert.True(body.GetProperty("rocAuc").GetDouble() > 0);
    }
}
