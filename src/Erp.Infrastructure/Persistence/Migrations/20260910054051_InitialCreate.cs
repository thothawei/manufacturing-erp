using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Erp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "bom_lines",
                columns: table => new
                {
                    ParentItemCode = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    ComponentItemCode = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    BomVersion = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    QtyPer = table.Column<decimal>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bom_lines", x => new { x.ParentItemCode, x.ComponentItemCode, x.BomVersion });
                });

            migrationBuilder.CreateTable(
                name: "inventory_balances",
                columns: table => new
                {
                    ItemCode = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    OnHandQty = table.Column<decimal>(type: "TEXT", nullable: false),
                    ReservedQty = table.Column<decimal>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inventory_balances", x => x.ItemCode);
                });

            migrationBuilder.CreateTable(
                name: "item_supply_infos",
                columns: table => new
                {
                    ItemCode = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    SupplierCode = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    LeadTimeDays = table.Column<int>(type: "INTEGER", nullable: false),
                    MinOrderQty = table.Column<decimal>(type: "TEXT", nullable: false),
                    OrderMultiple = table.Column<decimal>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_item_supply_infos", x => x.ItemCode);
                });

            migrationBuilder.CreateTable(
                name: "items",
                columns: table => new
                {
                    ItemCode = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    ItemName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ItemType = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Unit = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_items", x => x.ItemCode);
                });

            migrationBuilder.CreateTable(
                name: "purchase_orders",
                columns: table => new
                {
                    PoNo = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    SupplierCode = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    ItemCode = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    OrderedQty = table.Column<decimal>(type: "TEXT", nullable: false),
                    ReceivedQty = table.Column<decimal>(type: "TEXT", nullable: false),
                    ExpectedArrivalDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_purchase_orders", x => x.PoNo);
                });

            migrationBuilder.CreateTable(
                name: "quality_inspections",
                columns: table => new
                {
                    InspectionNo = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    WorkOrderNo = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    ItemCode = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    InspectedAt = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    InspectedQty = table.Column<decimal>(type: "TEXT", nullable: false),
                    PassedQty = table.Column<decimal>(type: "TEXT", nullable: false),
                    FailReason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_quality_inspections", x => x.InspectionNo);
                });

            migrationBuilder.CreateTable(
                name: "routing_steps",
                columns: table => new
                {
                    WorkOrderNo = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    StepNo = table.Column<int>(type: "INTEGER", nullable: false),
                    OperationName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    PlannedQty = table.Column<decimal>(type: "TEXT", nullable: false),
                    CompletedQty = table.Column<decimal>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_routing_steps", x => new { x.WorkOrderNo, x.StepNo });
                });

            migrationBuilder.CreateTable(
                name: "work_orders",
                columns: table => new
                {
                    WorkOrderNo = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    ItemCode = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    PlannedQty = table.Column<decimal>(type: "TEXT", nullable: false),
                    DueDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    MaterialIssueStatus = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_work_orders", x => x.WorkOrderNo);
                });

            migrationBuilder.CreateIndex(
                name: "IX_bom_lines_ParentItemCode",
                table: "bom_lines",
                column: "ParentItemCode");

            migrationBuilder.CreateIndex(
                name: "IX_items_ItemName",
                table: "items",
                column: "ItemName");

            migrationBuilder.CreateIndex(
                name: "IX_purchase_orders_ItemCode_Status",
                table: "purchase_orders",
                columns: new[] { "ItemCode", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_quality_inspections_ItemCode_InspectedAt",
                table: "quality_inspections",
                columns: new[] { "ItemCode", "InspectedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_quality_inspections_WorkOrderNo",
                table: "quality_inspections",
                column: "WorkOrderNo");

            migrationBuilder.CreateIndex(
                name: "IX_work_orders_Status_DueDate",
                table: "work_orders",
                columns: new[] { "Status", "DueDate" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "bom_lines");

            migrationBuilder.DropTable(
                name: "inventory_balances");

            migrationBuilder.DropTable(
                name: "item_supply_infos");

            migrationBuilder.DropTable(
                name: "items");

            migrationBuilder.DropTable(
                name: "purchase_orders");

            migrationBuilder.DropTable(
                name: "quality_inspections");

            migrationBuilder.DropTable(
                name: "routing_steps");

            migrationBuilder.DropTable(
                name: "work_orders");
        }
    }
}
