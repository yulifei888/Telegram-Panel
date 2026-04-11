using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TelegramPanel.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGroupCategories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CategoryId",
                table: "Groups",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "GroupCategories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupCategories", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Groups_CategoryId",
                table: "Groups",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_GroupCategories_Name",
                table: "GroupCategories",
                column: "Name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Groups_GroupCategories_CategoryId",
                table: "Groups",
                column: "CategoryId",
                principalTable: "GroupCategories",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Groups_GroupCategories_CategoryId",
                table: "Groups");

            migrationBuilder.DropTable(
                name: "GroupCategories");

            migrationBuilder.DropIndex(
                name: "IX_Groups_CategoryId",
                table: "Groups");

            migrationBuilder.DropColumn(
                name: "CategoryId",
                table: "Groups");
        }
    }
}
