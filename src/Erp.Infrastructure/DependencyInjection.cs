using Erp.Application.Abstractions;
using Erp.Infrastructure.AI;
using Erp.Infrastructure.Persistence;
using Erp.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, string connectionString)
    {
        services.AddDbContext<ErpDbContext>(options => options.UseSqlite(connectionString));

        services.AddScoped<IItemRepository, ItemRepository>();
        services.AddScoped<IInventoryRepository, InventoryRepository>();
        services.AddScoped<IBomRepository, BomRepository>();
        services.AddScoped<IWorkOrderRepository, WorkOrderRepository>();
        services.AddScoped<IPurchaseOrderRepository, PurchaseOrderRepository>();
        services.AddScoped<IQualityInspectionRepository, QualityInspectionRepository>();

        return services;
    }

    /// AI 助理是獨立的子系統，與 Persistence 平行註冊。
    /// 沒有設定 API key 時，SDK 會自行讀取 ANTHROPIC_API_KEY 環境變數。
    public static IServiceCollection AddAiAssistant(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AiAssistantOptions>(configuration.GetSection(AiAssistantOptions.SectionName));

        services.AddSingleton<ILlmClient, AnthropicLlmClient>();
        services.AddScoped<ToolDispatcher>();
        services.AddScoped<IAiAssistantService, AiAssistantService>();

        return services;
    }
}
