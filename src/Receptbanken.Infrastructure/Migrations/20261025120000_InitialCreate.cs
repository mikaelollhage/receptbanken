using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Receptbanken.Infrastructure.Migrations;

/// <inheritdoc />
public partial class InitialCreate : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Categories",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                ColorHex = table.Column<string>(type: "TEXT", maxLength: 9, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Categories", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Recipes",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                Description = table.Column<string>(type: "TEXT", nullable: true),
                SourceUrl = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                SourceHost = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                ImagePath = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                PrepTimeMinutes = table.Column<int>(type: "INTEGER", nullable: true),
                CookTimeMinutes = table.Column<int>(type: "INTEGER", nullable: true),
                Servings = table.Column<int>(type: "INTEGER", nullable: false),
                ServingsUnit = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                Notes = table.Column<string>(type: "TEXT", nullable: true),
                IsFavorite = table.Column<bool>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Recipes", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Ingredients",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                RecipeId = table.Column<int>(type: "INTEGER", nullable: false),
                OrderIndex = table.Column<int>(type: "INTEGER", nullable: false),
                RawText = table.Column<string>(type: "TEXT", nullable: false),
                Quantity = table.Column<decimal>(type: "TEXT", precision: 10, scale: 2, nullable: true),
                QuantityMax = table.Column<decimal>(type: "TEXT", precision: 10, scale: 2, nullable: true),
                Unit = table.Column<string>(type: "TEXT", maxLength: 30, nullable: true),
                Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                Preparation = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                GroupHeader = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Ingredients", x => x.Id);
                table.ForeignKey(
                    name: "FK_Ingredients_Recipes_RecipeId",
                    column: x => x.RecipeId,
                    principalTable: "Recipes",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "Instructions",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                RecipeId = table.Column<int>(type: "INTEGER", nullable: false),
                StepNumber = table.Column<int>(type: "INTEGER", nullable: false),
                InstructionText = table.Column<string>(type: "TEXT", nullable: false),
                GroupHeader = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Instructions", x => x.Id);
                table.ForeignKey(
                    name: "FK_Instructions_Recipes_RecipeId",
                    column: x => x.RecipeId,
                    principalTable: "Recipes",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "RecipeCategories",
            columns: table => new
            {
                RecipeId = table.Column<int>(type: "INTEGER", nullable: false),
                CategoryId = table.Column<int>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_RecipeCategories", x => new { x.RecipeId, x.CategoryId });
                table.ForeignKey(
                    name: "FK_RecipeCategories_Categories_CategoryId",
                    column: x => x.CategoryId,
                    principalTable: "Categories",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_RecipeCategories_Recipes_RecipeId",
                    column: x => x.RecipeId,
                    principalTable: "Recipes",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "ShoppingListItems",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                RecipeId = table.Column<int>(type: "INTEGER", nullable: true),
                RawText = table.Column<string>(type: "TEXT", nullable: false),
                Quantity = table.Column<decimal>(type: "TEXT", precision: 10, scale: 2, nullable: true),
                Unit = table.Column<string>(type: "TEXT", maxLength: 30, nullable: true),
                Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                IsChecked = table.Column<bool>(type: "INTEGER", nullable: false),
                Department = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ShoppingListItems", x => x.Id);
                table.ForeignKey(
                    name: "FK_ShoppingListItems_Recipes_RecipeId",
                    column: x => x.RecipeId,
                    principalTable: "Recipes",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Categories_Name",
            table: "Categories",
            column: "Name",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Ingredients_RecipeId",
            table: "Ingredients",
            column: "RecipeId");

        migrationBuilder.CreateIndex(
            name: "IX_Instructions_RecipeId",
            table: "Instructions",
            column: "RecipeId");

        migrationBuilder.CreateIndex(
            name: "IX_RecipeCategories_CategoryId",
            table: "RecipeCategories",
            column: "CategoryId");

        migrationBuilder.CreateIndex(
            name: "IX_Recipes_IsFavorite",
            table: "Recipes",
            column: "IsFavorite");

        migrationBuilder.CreateIndex(
            name: "IX_Recipes_Title",
            table: "Recipes",
            column: "Title");

        migrationBuilder.CreateIndex(
            name: "IX_ShoppingListItems_RecipeId",
            table: "ShoppingListItems",
            column: "RecipeId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "Ingredients");
        migrationBuilder.DropTable(name: "Instructions");
        migrationBuilder.DropTable(name: "RecipeCategories");
        migrationBuilder.DropTable(name: "ShoppingListItems");
        migrationBuilder.DropTable(name: "Categories");
        migrationBuilder.DropTable(name: "Recipes");
    }
}

