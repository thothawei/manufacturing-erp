using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Erp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPurchaseSuggestions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "purchase_suggestions",
                columns: table => new
                {
                    SuggestionNo = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    ItemCode = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    SuggestedQty = table.Column<decimal>(type: "TEXT", nullable: false),
                    SupplierCode = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    NeededByDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    CreatedOn = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    DecidedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    DecidedOn = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    CreatedPoNo = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_purchase_suggestions", x => x.SuggestionNo);
                });

            migrationBuilder.CreateIndex(
                name: "IX_purchase_suggestions_ItemCode_Status",
                table: "purchase_suggestions",
                columns: new[] { "ItemCode", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "purchase_suggestions");
        }
    }
}
