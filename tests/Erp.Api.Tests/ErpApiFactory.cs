using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Erp.Api.Tests;

/// 每個測試類別一個獨立的 SQLite 檔，避免互相干擾。
///
/// 已知觀察：這個測試專案剛建立後的第一次執行曾出現一次失敗，
/// 錯誤是啟動 seeding 時的 SqliteException。之後連續五次執行都通過，無法重現。
/// 已排除的原因：seeder 併發呼叫是安全的（第二個進來時 Items 已非空）、
/// 每個 factory 的連線字串確實指向各自的臨時檔（驗證過）。
/// 根因未確定，若再次出現請從這裡查起。
///
/// AI 助理的 BaseUrl 指向一個沒有服務在聽的埠 —— 這樣測 AI 端點的錯誤路徑時
/// 會走到連線失敗，不會真的打 Anthropic API（不花錢、不依賴網路）。
public sealed class ErpApiFactory : WebApplicationFactory<Program>
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"erp-api-test-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ErpDatabase"] = $"Data Source={_databasePath}",
                ["AiAssistant:ApiKey"] = "sk-ant-not-a-real-key-for-tests",
                ["AiAssistant:BaseUrl"] = "http://localhost:1",   // 沒有服務在聽
                ["AiAssistant:TimeoutSeconds"] = "5"
            }));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing && File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }
}
