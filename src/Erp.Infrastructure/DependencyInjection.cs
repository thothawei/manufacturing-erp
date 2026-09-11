using Erp.Application.Abstractions;
using Erp.Infrastructure.AI;
using Erp.Infrastructure.Persistence;
using Erp.Infrastructure.Persistence.Repositories;
using Erp.Infrastructure.Rag;
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

        // ToolDispatcher 在編譯期就相依 DocumentSearchService（第九個工具），
        // 所以這裡一併註冊 —— 分開讓呼叫端自己記得註冊，漏了只會在執行時才炸
        services.AddRag(configuration);

        return services;
    }

    /// 文件語意檢索。可選模組：沒裝 Ollama 時核心 ERP 與其他八個工具完全正常，
    /// 只有 search_documents 會回 SERVICE_UNAVAILABLE。
    ///
    /// 與 Persistence、AI 平行。方向是 AI → Rag → Persistence（Rag 用 ErpDbContext），
    /// Persistence 不得相依 Rag，由架構測試釘住。
    public static IServiceCollection AddRag(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RagOptions>(configuration.GetSection(RagOptions.SectionName));

        // HttpClient 開在這個類別裡面，跟 AnthropicLlmClient 同一個做法，所以是 singleton
        services.AddSingleton<IEmbeddingClient, OllamaEmbeddingClient>();
        services.AddScoped<DocumentSearchService>();
        services.AddScoped<RagIndexBuilder>();

        return services;
    }
}
