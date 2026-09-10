using Erp.Application.Abstractions;
using Erp.Infrastructure.Persistence;
using Erp.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
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
}
