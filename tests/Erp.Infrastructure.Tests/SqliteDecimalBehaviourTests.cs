using Erp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Erp.Infrastructure.Tests;

/// SQLite 上 decimal 的實際行為。
///
/// 欄位型別是 TEXT，直覺會以為比較會退化成字典序（"9" > "100"），
/// 但 EF Core 的 SQLite provider 會在連線上註冊 ef_compare()、ef_sum()
/// 與 EF_DECIMAL collation，所以透過 EF 查詢時數值語意是正確的。
///
/// 這組測試把這個行為釘住 —— 換資料庫 provider 或 EF 版本改變行為時會紅，
/// 而不是靜默地讓庫存判斷開始給錯答案。
public class SqliteDecimalBehaviourTests : IAsyncLifetime
{
    private SqliteTestDatabase _fixture = null!;

    public async Task InitializeAsync()
    {
        _fixture = new SqliteTestDatabase();

        // 刻意挑字典序與數值序不一致的組合："9" 的字典序大於 "100"
        _fixture.Db.InventoryBalances.AddRange(
            new() { ItemCode = "A", OnHandQty = 9m, ReservedQty = 0m },
            new() { ItemCode = "B", OnHandQty = 100m, ReservedQty = 0m },
            new() { ItemCode = "C", OnHandQty = 25.5m, ReservedQty = 0m });
        await _fixture.Db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task SQL層的大小比較是數值語意而不是字典序()
    {
        var db = _fixture.CreateContext();

        var result = await db.InventoryBalances
            .Where(b => b.OnHandQty > 50m)
            .Select(b => b.ItemCode)
            .ToListAsync();

        // 字典序的話 "9" > "50" 會成立，A 就會被錯誤地選進來
        Assert.Equal(["B"], result);
    }

    [Fact]
    public async Task SQL層的排序是數值語意()
    {
        var db = _fixture.CreateContext();

        var result = await db.InventoryBalances
            .OrderBy(b => b.OnHandQty)
            .Select(b => b.ItemCode)
            .ToListAsync();

        // 字典序會排成 [B(100), C(25.5), A(9)]
        Assert.Equal(["A", "C", "B"], result);
    }

    [Fact]
    public async Task SQL層的加總正確且不失精度()
    {
        var db = _fixture.CreateContext();

        var sum = await db.InventoryBalances.SumAsync(b => b.OnHandQty);

        Assert.Equal(134.5m, sum);
    }

    [Fact]
    public async Task 小數讀寫不失精度()
    {
        var db = _fixture.CreateContext();
        db.InventoryBalances.Add(new() { ItemCode = "D", OnHandQty = 0.1m + 0.2m, ReservedQty = 0m });
        await db.SaveChangesAsync();

        var read = await _fixture.CreateContext().InventoryBalances.SingleAsync(b => b.ItemCode == "D");

        // decimal 不是 double，0.1 + 0.2 就該是 0.3
        Assert.Equal(0.3m, read.OnHandQty);
    }

    [Fact]
    public async Task round_trip會保留小數位數_這是NormalizedDecimalConverter存在的原因()
    {
        var db = _fixture.CreateContext();
        db.InventoryBalances.Add(new() { ItemCode = "E", OnHandQty = 30m, ReservedQty = 0m });
        await db.SaveChangesAsync();

        var read = await _fixture.CreateContext().InventoryBalances.SingleAsync(b => b.ItemCode == "E");

        // 數值相等，但 scale 被保留了 —— 直接序列化會輸出 30.0 而不是 30
        Assert.Equal(30m, read.OnHandQty);
        Assert.Contains(".0", read.OnHandQty.ToString());
    }
}
