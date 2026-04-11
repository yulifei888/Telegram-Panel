using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TelegramPanel.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Groups_Accounts_CreatorAccountId",
                table: "Groups");

            migrationBuilder.AlterColumn<int>(
                name: "CreatorAccountId",
                table: "Groups",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.CreateTable(
                name: "AccountGroups",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AccountId = table.Column<int>(type: "INTEGER", nullable: false),
                    GroupId = table.Column<int>(type: "INTEGER", nullable: false),
                    IsCreator = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsAdmin = table.Column<bool>(type: "INTEGER", nullable: false),
                    SyncedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountGroups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccountGroups_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AccountGroups_Groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "Groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccountGroups_AccountId_GroupId",
                table: "AccountGroups",
                columns: new[] { "AccountId", "GroupId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccountGroups_GroupId",
                table: "AccountGroups",
                column: "GroupId");

            migrationBuilder.Sql(@"
INSERT OR IGNORE INTO AccountGroups (AccountId, GroupId, IsCreator, IsAdmin, SyncedAt)
SELECT CreatorAccountId, Id, 1, 1, SyncedAt
FROM Groups
WHERE CreatorAccountId IS NOT NULL;
");

            migrationBuilder.AddForeignKey(
                name: "FK_Groups_Accounts_CreatorAccountId",
                table: "Groups",
                column: "CreatorAccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Groups_Accounts_CreatorAccountId",
                table: "Groups");

            migrationBuilder.Sql(@"
UPDATE Groups
SET CreatorAccountId = (
    SELECT AccountId
    FROM AccountGroups
    WHERE GroupId = Groups.Id AND IsCreator = 1
    ORDER BY SyncedAt DESC
    LIMIT 1
)
WHERE CreatorAccountId IS NULL;
");

            migrationBuilder.Sql(@"DELETE FROM Groups WHERE CreatorAccountId IS NULL;");

            migrationBuilder.DropTable(
                name: "AccountGroups");

            migrationBuilder.AlterColumn<int>(
                name: "CreatorAccountId",
                table: "Groups",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "INTEGER",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Groups_Accounts_CreatorAccountId",
                table: "Groups",
                column: "CreatorAccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
