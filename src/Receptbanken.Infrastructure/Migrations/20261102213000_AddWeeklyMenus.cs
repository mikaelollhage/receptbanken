using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Receptbanken.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWeeklyMenus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WeeklyMenus",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WeekStartDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    IsoYear = table.Column<int>(type: "INTEGER", nullable: false),
                    IsoWeek = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    ConfirmedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WeeklyMenus", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WeeklyMenuDays",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WeeklyMenuId = table.Column<int>(type: "INTEGER", nullable: false),
                    DayIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    RecipeId = table.Column<int>(type: "INTEGER", nullable: true),
                    Servings = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WeeklyMenuDays", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WeeklyMenuDays_Recipes_RecipeId",
                        column: x => x.RecipeId,
                        principalTable: "Recipes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_WeeklyMenuDays_WeeklyMenus_WeeklyMenuId",
                        column: x => x.WeeklyMenuId,
                        principalTable: "WeeklyMenus",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WeeklyMenuPantryItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WeeklyMenuId = table.Column<int>(type: "INTEGER", nullable: false),
                    OrderIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    IngredientName = table.Column<string>(type: "TEXT", maxLength: 150, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WeeklyMenuPantryItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WeeklyMenuPantryItems_WeeklyMenus_WeeklyMenuId",
                        column: x => x.WeeklyMenuId,
                        principalTable: "WeeklyMenus",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WeeklyMenuDayCategories",
                columns: table => new
                {
                    WeeklyMenuDayId = table.Column<int>(type: "INTEGER", nullable: false),
                    CategoryId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WeeklyMenuDayCategories", x => new { x.WeeklyMenuDayId, x.CategoryId });
                    table.ForeignKey(
                        name: "FK_WeeklyMenuDayCategories_Categories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "Categories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WeeklyMenuDayCategories_WeeklyMenuDays_WeeklyMenuDayId",
                        column: x => x.WeeklyMenuDayId,
                        principalTable: "WeeklyMenuDays",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WeeklyMenuDayCategories_CategoryId",
                table: "WeeklyMenuDayCategories",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_WeeklyMenuDays_RecipeId",
                table: "WeeklyMenuDays",
                column: "RecipeId");

            migrationBuilder.CreateIndex(
                name: "IX_WeeklyMenuDays_WeeklyMenuId_Date",
                table: "WeeklyMenuDays",
                columns: new[] { "WeeklyMenuId", "Date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WeeklyMenuDays_WeeklyMenuId_DayIndex",
                table: "WeeklyMenuDays",
                columns: new[] { "WeeklyMenuId", "DayIndex" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WeeklyMenuPantryItems_WeeklyMenuId",
                table: "WeeklyMenuPantryItems",
                column: "WeeklyMenuId");

            migrationBuilder.CreateIndex(
                name: "IX_WeeklyMenus_IsoYear_IsoWeek",
                table: "WeeklyMenus",
                columns: new[] { "IsoYear", "IsoWeek" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WeeklyMenus_Status",
                table: "WeeklyMenus",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_WeeklyMenus_WeekStartDate",
                table: "WeeklyMenus",
                column: "WeekStartDate",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WeeklyMenuDayCategories");

            migrationBuilder.DropTable(
                name: "WeeklyMenuPantryItems");

            migrationBuilder.DropTable(
                name: "WeeklyMenuDays");

            migrationBuilder.DropTable(
                name: "WeeklyMenus");
        }
    }
}

