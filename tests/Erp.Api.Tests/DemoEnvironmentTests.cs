using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Erp.Api.Tests;

/// 線上展示站用的是 `ASPNETCORE_ENVIRONMENT=Demo`，不是 Development。
public sealed class DemoApiFactory : ErpApiFactory
{
    protected override string Environment => "Demo";
}

/// 展示環境的行為。
///
/// 這組測試存在的理由很具體：部署到公開網址之後才發現「Scalar 沒開」或
/// 「資料表是空的」，是最晚、也最難看的失敗 —— 而那只需要一個環境名稱打錯字就會發生。
/// Demo 是一個獨立的環境名稱（不是 Development 的別名），所以它的行為要單獨驗。
public class DemoEnvironmentTests(DemoApiFactory factory) : IClassFixture<DemoApiFactory>
{
    private HttpClient Client => factory.CreateClient();

    [Fact]
    public async Task 展示環境會灌入展示資料()
    {
        // 空資料庫的展示站等於一個壞掉的連結
        var response = await Client.GetAsync("/api/items/PANEL-01/inventory");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(80, body.GetProperty("availableQty").GetInt32());
    }

    [Fact]
    public async Task 展示環境開放Scalar互動文件()
    {
        var response = await Client.GetAsync("/scalar/v1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task 首頁回的是導覽而不是404()
    {
        // 訪客打開網址第一眼看到的東西
        var response = await Client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("線上展示", body.GetProperty("name").GetString()!);
        Assert.NotEmpty(body.GetProperty("tryThese").EnumerateArray());
    }

    [Fact]
    public async Task 首頁誠實說明哪些功能在這個環境下不會動()
    {
        // 「它壞了」與「它刻意不開」是兩件事，訪客分得出來才不會誤會
        var body = await Client.GetFromJsonAsync<JsonElement>("/");

        var unavailable = string.Join("\n",
            body.GetProperty("notAvailableHere").EnumerateArray().Select(x => x.GetString()));

        Assert.Contains("ai-assistant", unavailable);
        Assert.Contains("RAG", unavailable);
    }

    [Fact]
    public async Task ML延遲風險預測在展示環境可用()
    {
        // 模型檔有沒有跟著發佈出去，只有真的打一次才知道。
        //
        // 工單號從 at-risk 端點拿而不是自己用今天的日期拼：種子資料的單號
        // 是以「執行當天」產生的，自己拼會在跨日或時區邊界上莫名其妙地 404，
        // 而那個 404 與這條測試想驗的事情完全無關。
        var atRisk = await Client.GetFromJsonAsync<JsonElement>("/api/work-orders/at-risk");
        var workOrderNo = atRisk.EnumerateArray().First().GetProperty("workOrderNo").GetString();

        var body = await Client.GetFromJsonAsync<JsonElement>(
            $"/api/work-orders/{workOrderNo}/delay-risk");

        Assert.True(body.GetProperty("predictedDelayProbability").ValueKind != JsonValueKind.Null,
            "模型沒有載起來 —— 檢查 ONNX 檔案有沒有跟著發佈輸出走");
    }

    [Fact]
    public async Task 展示工單的分布外特徵會誠實出現在回應裡()
    {
        // 展示資料只有四張工單，weekly_load_ratio 算出來是 0.125，
        // 而訓練資料的下界是 0.5 —— 也就是說**展示站上看到的每一個機率，
        // 都是模型對沒見過的輸入給出來的**。這件事必須在回應裡看得見，
        // 而不是只寫在文件的已知限制裡。
        var atRisk = await Client.GetFromJsonAsync<JsonElement>("/api/work-orders/at-risk");
        var workOrderNo = atRisk.EnumerateArray().First().GetProperty("workOrderNo").GetString();

        var body = await Client.GetFromJsonAsync<JsonElement>(
            $"/api/work-orders/{workOrderNo}/delay-risk");

        var outOfDistribution = body.GetProperty("outOfDistributionFeatures").EnumerateArray().ToList();

        Assert.Contains(outOfDistribution,
            f => f.GetProperty("feature").GetString() == "weekly_load_ratio");
        Assert.Contains("分布之外", body.GetProperty("note").GetString()!);
    }
}
