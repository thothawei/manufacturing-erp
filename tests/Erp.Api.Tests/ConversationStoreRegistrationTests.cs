using Erp.Infrastructure.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Api.Tests;

/// 對話記憶是跨請求的狀態，所以在 DI 裡必須是 singleton。
///
/// 這條防線存在的理由很具體：註冊成 scoped 的話，每個 HTTP 請求都會拿到一個空的 store，
/// 對話記憶會**安靜地完全失效** —— 沒有例外、沒有錯誤，只是每次追問都像是新對話。
/// 而所有 Infrastructure 層的測試都是自己 new 一個 store 傳進去的，
/// 它們一條都不會紅。這種「測試全綠但功能沒作用」的缺口，只有在組裝處驗才擋得住。
public class ConversationStoreRegistrationTests(ErpApiFactory factory) : IClassFixture<ErpApiFactory>
{
    [Fact]
    public void 對話記憶在不同請求範圍之間是同一個實例()
    {
        using var first = factory.Services.CreateScope();
        using var second = factory.Services.CreateScope();

        Assert.Same(
            first.ServiceProvider.GetRequiredService<IConversationStore>(),
            second.ServiceProvider.GetRequiredService<IConversationStore>());
    }

    [Fact]
    public void 對話記憶跨請求範圍記得住內容()
    {
        // 上一條驗的是「同一個實例」，這條驗的是那件事真正的後果。
        // 分成兩條的理由：就算之後換成別的實作（例如共用的分散式快取），
        // 實例可以不同，但「記得住」這件事不能變。
        var id = Guid.NewGuid().ToString();

        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<IConversationStore>()
                .Append(id, new ConversationTurn("面板還有多少？", "可用 80 片。"));
        }

        using (var scope = factory.Services.CreateScope())
        {
            var turn = Assert.Single(
                scope.ServiceProvider.GetRequiredService<IConversationStore>().GetRecentTurns(id));

            Assert.Equal("面板還有多少？", turn.Question);
        }
    }
}
