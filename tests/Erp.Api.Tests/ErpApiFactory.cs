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
public class ErpApiFactory : WebApplicationFactory<Program>
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"erp-api-test-{Guid.NewGuid():N}.db");

    /// 線上展示站跑的是 Demo 而不是 Development。
    /// 兩者行為應該一致（開放 Scalar、啟動時灌展示資料），而那件事需要被驗證 ——
    /// 部署上去才發現 Scalar 沒開、或資料表是空的，是最晚才會發現的失敗。
    protected virtual string Environment => "Development";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environment);

        // 用 UseSetting 而不是 ConfigureAppConfiguration.AddInMemoryCollection：
        // 後者加進去的來源會被之後載入的 appsettings.{Environment}.json 蓋掉，
        // 而那個失敗完全沒有症狀 —— 測試照樣全綠，只是全部共用
        // bin/ 底下那個相對路徑的 erp.db，連跨天殘留的舊種子資料都一起繼承。
        // （這個 bug 真的發生過：2026-09-14 的測試讀到的是 09-10 灌的工單。）
        // UseSetting 寫的是 host configuration，優先級高於任何 appsettings 檔案。
        foreach (var (key, value) in new Dictionary<string, string>
        {
            ["ConnectionStrings:ErpDatabase"] = $"Data Source={_databasePath}",
            ["AiAssistant:ApiKey"] = "sk-ant-not-a-real-key-for-tests",
            ["AiAssistant:BaseUrl"] = "http://localhost:1",   // 沒有服務在聽
            ["AiAssistant:TimeoutSeconds"] = "5",
            ["Rag:OllamaBaseUrl"] = "http://localhost:1",     // 沒有服務在聽
            ["Rag:TimeoutSeconds"] = "2"
        })
        {
            builder.UseSetting(key, value);
        }
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
