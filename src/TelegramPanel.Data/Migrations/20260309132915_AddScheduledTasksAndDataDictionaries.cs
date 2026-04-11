using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TelegramPanel.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddScheduledTasksAndDataDictionaries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "SystemCreatedAtUtc",
                table: "Groups",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SystemCreatedAtUtc",
                table: "Channels",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DataDictionaries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Type = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    ReadMode = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    NextIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataDictionaries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ScheduledTasks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TaskType = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Total = table.Column<int>(type: "INTEGER", nullable: false),
                    ConfigJson = table.Column<string>(type: "TEXT", nullable: true),
                    CronExpression = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    NextRunAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastRunAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastBatchTaskId = table.Column<int>(type: "INTEGER", nullable: true),
                    OwnedAssetScopeId = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduledTasks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DataDictionaryItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DataDictionaryId = table.Column<int>(type: "INTEGER", nullable: false),
                    TextValue = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    AssetPath = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataDictionaryItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DataDictionaryItems_DataDictionaries_DataDictionaryId",
                        column: x => x.DataDictionaryId,
                        principalTable: "DataDictionaries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DataDictionaries_IsEnabled",
                table: "DataDictionaries",
                column: "IsEnabled");

            migrationBuilder.CreateIndex(
                name: "IX_DataDictionaries_Name",
                table: "DataDictionaries",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DataDictionaries_Type",
                table: "DataDictionaries",
                column: "Type");

            migrationBuilder.CreateIndex(
                name: "IX_DataDictionaryItems_DataDictionaryId",
                table: "DataDictionaryItems",
                column: "DataDictionaryId");

            migrationBuilder.CreateIndex(
                name: "IX_DataDictionaryItems_DataDictionaryId_SortOrder",
                table: "DataDictionaryItems",
                columns: new[] { "DataDictionaryId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledTasks_CreatedAt",
                table: "ScheduledTasks",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledTasks_NextRunAtUtc",
                table: "ScheduledTasks",
                column: "NextRunAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledTasks_Status",
                table: "ScheduledTasks",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DataDictionaryItems");

            migrationBuilder.DropTable(
                name: "ScheduledTasks");

            migrationBuilder.DropTable(
                name: "DataDictionaries");

            migrationBuilder.DropColumn(
                name: "SystemCreatedAtUtc",
                table: "Groups");

            migrationBuilder.DropColumn(
                name: "SystemCreatedAtUtc",
                table: "Channels");
        }
    }
}
