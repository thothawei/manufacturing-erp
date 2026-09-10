using Erp.Application.Common;
using Erp.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Erp.Infrastructure.Tests;

public sealed class TestClock(DateOnly today) : IClock
{
    public DateOnly Today { get; } = today;
}

/// 每個測試一個獨立的 in-memory SQLite 資料庫。
/// 連線一關資料就消失，所以 fixture 必須持有連線直到測試結束。
/// 這裡刻意用 Migrate 而不是 EnsureCreated —— 順便驗證 migration 本身跑得起來。
public sealed class SqliteTestDatabase : IAsyncDisposable
{
    private readonly SqliteConnection _connection;

    public SqliteTestDatabase()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        Db = CreateContext();
        Db.Database.Migrate();
    }

    public ErpDbContext Db { get; }

    /// 需要驗證「寫進去再讀出來」時，用另一個 context 讀，避免 EF 的追蹤快取讓測試失真
    public ErpDbContext CreateContext()
        => new(new DbContextOptionsBuilder<ErpDbContext>().UseSqlite(_connection).Options);

    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
