using Erp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Erp.Infrastructure.Tests;

/// 併發灌種子資料。
///
/// 這裡刻意用「檔案」SQLite 而不是 in-memory —— in-memory 資料庫共用單一連線，
/// 寫入天然被序列化，測不出這個問題。實際踩到的情境是 WebApplicationFactory
/// 建立 host 不只一次，兩次 seeding 在冷啟動時真正重疊，撞上
/// UNIQUE constraint failed: bom_lines...
///
/// 這不只是測試環境的問題：多個 API 實例同時啟動時，生產環境是一樣的競態。
public class SeederConcurrencyTests : IDisposable
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"seed-race-{Guid.NewGuid():N}.db");

    private ErpDbContext CreateContext()
        => new(new DbContextOptionsBuilder<ErpDbContext>()
            .UseSqlite($"Data Source={_databasePath}")
            .Options);

    public SeederConcurrencyTests()
    {
        using var db = CreateContext();
        db.Database.Migrate();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task 四個實例同時灌種子資料不會擲例外()
    {
        var clock = new TestClock(new DateOnly(2026, 9, 10));

        // 各自獨立的 context 與連線，才會真的併發寫入
        var tasks = Enumerable.Range(0, 4)
            .Select(_ => Task.Run(async () =>
            {
                await using var db = CreateContext();
                await ErpDbSeeder.SeedAsync(db, clock);
            }))
            .ToArray();

        await Task.WhenAll(tasks);   // 任何一個拋例外，這裡就會紅
    }

    [Fact]
    public async Task 併發灌完之後資料不重複也不缺漏()
    {
        var clock = new TestClock(new DateOnly(2026, 9, 10));

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            await using var db = CreateContext();
            await ErpDbSeeder.SeedAsync(db, clock);
        })));

        await using var check = CreateContext();
        Assert.Equal(6, await check.Items.CountAsync());
        Assert.Equal(6, await check.BomLines.CountAsync());
        Assert.Equal(4, await check.WorkOrders.CountAsync());
        Assert.Equal(3, await check.PurchaseOrders.CountAsync());
        Assert.Equal(3, await check.QualityInspections.CountAsync());
    }

    [Fact]
    public async Task 序列重複呼叫仍然是冪等的()
    {
        var clock = new TestClock(new DateOnly(2026, 9, 10));

        for (var i = 0; i < 3; i++)
        {
            await using var db = CreateContext();
            await ErpDbSeeder.SeedAsync(db, clock);
        }

        await using var check = CreateContext();
        Assert.Equal(6, await check.Items.CountAsync());
    }
}
