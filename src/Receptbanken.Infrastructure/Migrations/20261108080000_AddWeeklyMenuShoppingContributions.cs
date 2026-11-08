using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Receptbanken.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWeeklyMenuShoppingContributions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WeeklyMenuShoppingContributions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WeeklyMenuId = table.Column<int>(type: "INTEGER", nullable: false),
                    WeekStartDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    DayIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    RecipeId = table.Column<int>(type: "INTEGER", nullable: false),
                    ContributionKey = table.Column<string>(type: "TEXT", maxLength: 250, nullable: false),
                    TargetServings = table.Column<int>(type: "INTEGER", nullable: false),
                    AddedQuantity = table.Column<decimal>(type: "TEXT", precision: 10, scale: 2, nullable: true),
                    AddedQuantityMax = table.Column<decimal>(type: "TEXT", precision: 10, scale: 2, nullable: true),
                    Unit = table.Column<string>(type: "TEXT", maxLength: 30, nullable: true),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WeeklyMenuShoppingContributions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WeeklyMenuShoppingContributions_WeeklyMenus_WeeklyMenuId",
                        column: x => x.WeeklyMenuId,
                        principalTable: "WeeklyMenus",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WeeklyMenuShoppingContributions_WeeklyMenuId_DayIndex_RecipeId_ContributionKey",
                table: "WeeklyMenuShoppingContributions",
                columns: new[] { "WeeklyMenuId", "DayIndex", "RecipeId", "ContributionKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WeeklyMenuShoppingContributions_WeekStartDate",
                table: "WeeklyMenuShoppingContributions",
                column: "WeekStartDate");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WeeklyMenuShoppingContributions");
        }
    }
}
