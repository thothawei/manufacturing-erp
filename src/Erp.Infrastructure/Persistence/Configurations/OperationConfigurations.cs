using Erp.Domain.Production;
using Erp.Domain.Purchasing;
using Erp.Domain.Quality;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Erp.Infrastructure.Persistence.Configurations;

public sealed class WorkOrderConfiguration : IEntityTypeConfiguration<WorkOrder>
{
    public void Configure(EntityTypeBuilder<WorkOrder> builder)
    {
        builder.ToTable("work_orders");
        builder.HasKey(w => w.WorkOrderNo);
        builder.Property(w => w.WorkOrderNo).HasMaxLength(50);
        builder.Property(w => w.ItemCode).HasMaxLength(50).IsRequired();
        builder.Property(w => w.MaterialIssueStatus).HasMaxLength(50);
        builder.Property(w => w.Status).HasConversion<string>().HasMaxLength(20);

        builder.Ignore(w => w.IsOpen);

        // 風險判定與 MRP 都以「狀態 + 交期」撈工單
        builder.HasIndex(w => new { w.Status, w.DueDate });
    }
}

public sealed class RoutingStepConfiguration : IEntityTypeConfiguration<RoutingStep>
{
    public void Configure(EntityTypeBuilder<RoutingStep> builder)
    {
        builder.ToTable("routing_steps");
        builder.HasKey(s => new { s.WorkOrderNo, s.StepNo });
        builder.Property(s => s.WorkOrderNo).HasMaxLength(50);
        builder.Property(s => s.OperationName).HasMaxLength(100).IsRequired();
        builder.Property(s => s.Status).HasConversion<string>().HasMaxLength(20);
    }
}

public sealed class PurchaseOrderConfiguration : IEntityTypeConfiguration<PurchaseOrder>
{
    public void Configure(EntityTypeBuilder<PurchaseOrder> builder)
    {
        builder.ToTable("purchase_orders");
        builder.HasKey(p => p.PoNo);
        builder.Property(p => p.PoNo).HasMaxLength(50);
        builder.Property(p => p.SupplierCode).HasMaxLength(50).IsRequired();
        builder.Property(p => p.ItemCode).HasMaxLength(50).IsRequired();
        builder.Property(p => p.Status).HasConversion<string>().HasMaxLength(20);

        builder.Ignore(p => p.InTransitQty);
        builder.Ignore(p => p.IsOpen);

        builder.HasIndex(p => new { p.ItemCode, p.Status });
    }
}

public sealed class QualityInspectionConfiguration : IEntityTypeConfiguration<QualityInspection>
{
    public void Configure(EntityTypeBuilder<QualityInspection> builder)
    {
        builder.ToTable("quality_inspections");
        builder.HasKey(q => q.InspectionNo);
        builder.Property(q => q.InspectionNo).HasMaxLength(50);
        builder.Property(q => q.WorkOrderNo).HasMaxLength(50).IsRequired();
        builder.Property(q => q.ItemCode).HasMaxLength(50).IsRequired();
        builder.Property(q => q.FailReason).HasMaxLength(500);

        builder.Ignore(q => q.FailedQty);

        builder.HasIndex(q => q.WorkOrderNo);
        builder.HasIndex(q => new { q.ItemCode, q.InspectedAt });
    }
}
