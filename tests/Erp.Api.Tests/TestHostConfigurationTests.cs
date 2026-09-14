using Erp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Api.Tests;

/// 測試主機的設定有沒有真的生效。
///
/// 這組測試存在的理由是一個**完全沒有症狀**的失敗：
/// `ErpApiFactory` 原本用 `ConfigureAppConfiguration.AddInMemoryCollection` 指定
/// 連線字串，而那個來源會被之後載入的 `appsettings.{Environment}.json` 蓋掉。
/// 結果是所有測試類別共用 `bin/` 底下那個相對路徑的 `erp.db`，
/// 連跨天殘留的舊種子資料都一起繼承 —— 而測試照樣全綠。
///
/// 實際被咬到的樣子：2026-09-14 跑的測試讀到的是 09-10 灌的工單，
/// 用今天的日期去查工單號會 404。工廠註解宣稱的「每個測試類別一個獨立 SQLite 檔」
/// 那時已經有一年沒有成立過，沒有任何一條測試會紅。
///
/// 反向驗證：把 `UseSetting` 改回 `AddInMemoryCollection`，這兩條會紅，
/// 而其他 33 條照樣全綠。
public class TestHostConfigurationTests(ErpApiFactory factory) : IClassFixture<ErpApiFactory>
{
    private IConfiguration Configuration =>
        factory.Services.GetRequiredService<IConfiguration>();

    /// **要驗的是 DbContext 真正連到哪個檔案，不是設定表裡有什麼。**
    ///
    /// 這兩件事會不一致，而這條測試第一版就踩到了：`AddInMemoryCollection` 加的來源
    /// 確實出現在最終的 IConfiguration 裡（所以讀設定表會是對的），
    /// 但 `Program.cs` 在 `builder.Build()` 之前就把連線字串讀走了 ——
    /// 那時測試工廠的設定還沒注入。於是設定表說 temp 檔、實際連的是 bin 下的 erp.db。
    ///
    /// 第一版測試讀設定表，在壞掉的版本上照樣是綠的。是反向驗證抓到它驗錯了層級。
    [Fact]
    public void DbContext真正連到的是每個測試類別各自的暫存檔()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ErpDbContext>();

        var connectionString = db.Database.GetConnectionString();

        Assert.Contains("erp-api-test-", connectionString);
    }

    [Fact]
    public void 外部服務都指向沒有服務在聽的埠()
    {
        // 測試不該真的打到 Anthropic 或 Ollama。這兩個設定被蓋掉的話，
        // 測試會依「這台機器有沒有裝 Ollama」而有不同行為
        Assert.Equal("http://localhost:1", Configuration["AiAssistant:BaseUrl"]);
        Assert.Equal("http://localhost:1", Configuration["Rag:OllamaBaseUrl"]);
    }
}
