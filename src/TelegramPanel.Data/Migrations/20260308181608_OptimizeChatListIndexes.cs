using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TelegramPanel.Data.Migrations
{
    /// <inheritdoc />
    public partial class OptimizeChatListIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Groups_SyncedAt",
                table: "Groups",
                column: "SyncedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Channels_SyncedAt",
                table: "Channels",
                column: "SyncedAt");

            migrationBuilder.CreateIndex(
                name: "IX_AccountGroups_GroupId_IsCreator_IsAdmin",
                table: "AccountGroups",
                columns: new[] { "GroupId", "IsCreator", "IsAdmin" });

            migrationBuilder.CreateIndex(
                name: "IX_AccountChannels_ChannelId_IsCreator_IsAdmin",
                table: "AccountChannels",
                columns: new[] { "ChannelId", "IsCreator", "IsAdmin" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Groups_SyncedAt",
                table: "Groups");

            migrationBuilder.DropIndex(
                name: "IX_Channels_SyncedAt",
                table: "Channels");

            migrationBuilder.DropIndex(
                name: "IX_AccountGroups_GroupId_IsCreator_IsAdmin",
                table: "AccountGroups");

            migrationBuilder.DropIndex(
                name: "IX_AccountChannels_ChannelId_IsCreator_IsAdmin",
                table: "AccountChannels");
        }
    }
}
