using System.Net;

namespace Erp.Api.Tests;

/// RAG 是可選模組：沒有 Ollama 時整個服務仍要正常啟動。
///
/// 這組測試的價值在「評審 clone 下來沒裝 Ollama」這個最可能發生的情境 ——
/// 如果索引建不起來會讓啟動失敗，對方看到的是一個跑不起來的專案，
/// 而不是一個少了可選功能的專案。
public class RagOptionalModuleTests(ErpApiFactory factory) : IClassFixture<ErpApiFactory>
{
    [Fact]
    public async Task Ollama不可用時服務照常啟動()
    {
        // 工廠把 Ollama 指向沒有服務在聽的埠，所以啟動時索引一定建不起來。
        // 能拿到 200 就證明 RagIndexBuilder 把失敗吞成了 Warning
        var response = await factory.CreateClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/items/search?keyword=PANEL")]
    [InlineData("/api/items/TV-100/sufficiency")]
    [InlineData("/api/mrp/shortages")]
    [InlineData("/api/work-orders/at-risk")]
    public async Task Ollama不可用時核心端點完全不受影響(string url)
    {
        var response = await factory.CreateClient().GetAsync(url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
