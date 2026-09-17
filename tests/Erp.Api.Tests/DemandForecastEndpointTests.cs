using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Erp.Application.Ml;

namespace Erp.Api.Tests;

/// /api/ml/demand-forecast 走真的 HTTP，接的是 repo 裡真正的 GBDT 模型（S6）。
public class DemandForecastEndpointTests(ErpApiFactory factory) : IClassFixture<ErpApiFactory>
{
    private HttpClient Client => factory.CreateClient();

    [Fact]
    public async Task 已知品項回傳seasonal_naive與模型預測()
    {
        var response = await Client.PostAsJsonAsync("/api/ml/demand-forecast", new
        {
            itemCode = "PANEL-01",
            lag1 = 240.0,
            lag2 = 235.0,
            lag3 = 250.0,
            lag4 = 245.0,
            lag52 = 230.0,
            rollingMean4 = 242.5,
            rollingMean12 = 238.0,
            weekOfYear = 10
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("PANEL-01", body.GetProperty("itemCode").GetString());
        // seasonal naive 就是呼叫端給的 lag52，不經過模型，一定要原封不動地回來
        Assert.Equal(230.0, body.GetProperty("seasonalNaiveDemand").GetDouble());
        Assert.True(body.GetProperty("modelAvailable").GetBoolean(), body.ToString());
        Assert.True(body.GetProperty("predictedDemand").GetDouble() > 0);
        Assert.Equal("gbdt", body.GetProperty("deployedModel").GetString());
    }

    [Fact]
    public async Task 不認得的料號回400()
    {
        var response = await Client.PostAsJsonAsync("/api/ml/demand-forecast", new
        {
            itemCode = "TV-100",   // 有效料號，但不是這個模型訓練過的三個物料之一
            lag1 = 1.0,
            lag2 = 1.0,
            lag3 = 1.0,
            lag4 = 1.0,
            lag52 = 1.0,
            rollingMean4 = 1.0,
            rollingMean12 = 1.0,
            weekOfYear = 0
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task 健康檢查回傳模型註冊資訊()
    {
        var response = await Client.GetAsync("/api/ml/demand-forecast-health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(body.GetProperty("available").GetBoolean(), body.ToString());
        Assert.True(body.GetProperty("featureSchemaConsistent").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("trainedOn").GetString()));
        Assert.Equal("gbdt", body.GetProperty("deployedModel").GetString());
        Assert.True(body.TryGetProperty("drift", out _));
    }

    /// 累積到門檻（30 筆）以上，drift 就該對每個特徵都算出 PSI，
    /// 不是只回傳「樣本夠了」這種狀態旗標 —— 驗的是真的算得出來，不是有沒有這個欄位。
    [Fact]
    public async Task 累積足夠樣本後drift回報每個特徵的PSI()
    {
        for (var i = 0; i < 30; i++)
        {
            var response = await Client.PostAsJsonAsync("/api/ml/demand-forecast", new
            {
                itemCode = "PANEL-01",
                lag1 = 240.0 + i,
                lag2 = 235.0,
                lag3 = 250.0,
                lag4 = 245.0,
                lag52 = 230.0,
                rollingMean4 = 242.5,
                rollingMean12 = 238.0,
                weekOfYear = 10
            });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var healthResponse = await Client.GetAsync("/api/ml/demand-forecast-health");
        var body = await healthResponse.Content.ReadFromJsonAsync<JsonElement>();

        var drift = body.GetProperty("drift");
        Assert.True(drift.GetProperty("available").GetBoolean(), drift.ToString());
        Assert.True(drift.GetProperty("recentSampleCount").GetInt32() >= 30);

        var perFeaturePsi = drift.GetProperty("perFeaturePsi");
        foreach (var name in MaterialDemandForecastFeatures.FeatureNames)
        {
            Assert.True(perFeaturePsi.TryGetProperty(name, out _), $"缺少 {name} 的 PSI");
        }
    }
}
