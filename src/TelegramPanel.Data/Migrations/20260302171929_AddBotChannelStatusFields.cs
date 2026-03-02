using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TelegramPanel.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBotChannelStatusFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ChannelStatusCheckedAtUtc",
                table: "BotChannels",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ChannelStatusError",
                table: "BotChannels",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ChannelStatusOk",
                table: "BotChannels",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_BotChannels_ChannelStatusOk",
                table: "BotChannels",
                column: "ChannelStatusOk");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BotChannels_ChannelStatusOk",
                table: "BotChannels");

            migrationBuilder.DropColumn(
                name: "ChannelStatusCheckedAtUtc",
                table: "BotChannels");

            migrationBuilder.DropColumn(
                name: "ChannelStatusError",
                table: "BotChannels");

            migrationBuilder.DropColumn(
                name: "ChannelStatusOk",
                table: "BotChannels");
        }
    }
}
