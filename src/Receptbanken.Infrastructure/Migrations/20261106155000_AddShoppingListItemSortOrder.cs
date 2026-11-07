using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Receptbanken.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddShoppingListItemSortOrder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SortOrder",
                table: "ShoppingListItems",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql("""
                UPDATE ShoppingListItems
                SET SortOrder = (
                    SELECT COUNT(*)
                    FROM ShoppingListItems AS s2
                    WHERE (s2.CreatedAt < ShoppingListItems.CreatedAt)
                       OR (s2.CreatedAt = ShoppingListItems.CreatedAt AND s2.Id <= ShoppingListItems.Id)
                )
                WHERE SortOrder = 0;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SortOrder",
                table: "ShoppingListItems");
        }
    }
}
