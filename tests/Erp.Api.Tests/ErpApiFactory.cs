using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Erp.Api.Tests;

/// 每個測試類別一個獨立的 SQLite 檔，避免互相干擾。
///
/// 曾經的 flaky（已修）：症狀是「每個 build 組態的第一次執行才失敗」，
/// 錯誤 UNIQUE constraint failed: bom_lines...。
/// 根因是 WebApplicationFactory 會建立 host 不只一次，冷啟動時兩次 seeding 真正重疊，
/// 雙方都通過了「是否已有資料」的檢查；熱身後第一次太快完成，第二次就只看到資料而跳過。
/// 修在 ErpDbSeeder（容忍併發衝突），由 SeederConcurrencyTests 把關。
///
/// Rag 的 OllamaBaseUrl 也指向沒有服務在聽的埠，理由同上，外加一個：
/// 不這樣固定的話，測試行為會隨「這台機器有沒有裝 Ollama」而漂。
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
                ["AiAssistant:TimeoutSeconds"] = "5",
                ["Rag:OllamaBaseUrl"] = "http://localhost:1",     // 沒有服務在聽
                ["Rag:TimeoutSeconds"] = "2"
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
