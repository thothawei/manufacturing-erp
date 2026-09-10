using Erp.Application.Bom;
using Erp.Application.Common;
using Erp.Application.Inventory;
using Erp.Application.Items;
using Erp.Application.Mrp;
using Erp.Application.Production;
using Erp.Application.Purchasing;
using Erp.Application.Quality;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Application;

public static class DependencyInjection
{
    /// 註冊 Application 層服務。
    /// Repository 介面的實作由 Infrastructure 層自行註冊（依賴反轉）。
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton<IClock, SystemClock>();

        services.AddScoped<ItemMasterQueryService>();
        services.AddScoped<InventoryQueryService>();
        services.AddScoped<BomExplosionService>();
        services.AddScoped<WorkOrderProgressService>();
        services.AddScoped<WorkOrderRiskService>();
        services.AddScoped<MrpCalculationService>();
        services.AddScoped<PurchasingQueryService>();
        services.AddScoped<QualityInspectionQueryService>();

        return services;
    }
}
