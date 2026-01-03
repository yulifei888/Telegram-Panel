using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TelegramPanel.Data.Migrations
{
    /// <inheritdoc />
    public partial class GlobalizeBotChannels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 将“每个 Bot 一套 BotChannels/BotChannelCategories”的旧结构，升级为：
            // - BotChannels：全局唯一（TelegramId 唯一）
            // - BotChannelCategories：全局唯一（Name 唯一）
            // - BotChannelMembers：Bot-频道关系（哪个 Bot 在哪个频道里）
            //
            // 分类合并策略（用户选择 A）：同一 TelegramId 在多 Bot 下若存在不同分类，
            // 取“出现次数最多”的分类作为全局分类。

            migrationBuilder.Sql("ALTER TABLE BotChannels RENAME TO BotChannels_Old;");
            migrationBuilder.Sql("ALTER TABLE BotChannelCategories RENAME TO BotChannelCategories_Old;");

            // SQLite：表重命名后索引名不会自动变更；为避免与新表的同名索引冲突，这里先删旧索引（数据复制不依赖它们）。
            migrationBuilder.Sql("DROP INDEX IF EXISTS IX_BotChannels_BotId_TelegramId;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS IX_BotChannels_Username;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS IX_BotChannels_CategoryId;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS IX_BotChannelCategories_BotId_Name;");

            migrationBuilder.CreateTable(
                name: "BotChannelCategories",
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
                    table.PrimaryKey("PK_BotChannelCategories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BotChannels",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TelegramId = table.Column<long>(type: "INTEGER", nullable: false),
                    AccessHash = table.Column<long>(type: "INTEGER", nullable: true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Username = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    IsBroadcast = table.Column<bool>(type: "INTEGER", nullable: false),
                    MemberCount = table.Column<int>(type: "INTEGER", nullable: false),
                    About = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SyncedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CategoryId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BotChannels", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BotChannels_BotChannelCategories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "BotChannelCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "BotChannelMembers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BotId = table.Column<int>(type: "INTEGER", nullable: false),
                    BotChannelId = table.Column<int>(type: "INTEGER", nullable: false),
                    SyncedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BotChannelMembers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BotChannelMembers_BotChannels_BotChannelId",
                        column: x => x.BotChannelId,
                        principalTable: "BotChannels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BotChannelMembers_Bots_BotId",
                        column: x => x.BotId,
                        principalTable: "Bots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BotChannelCategories_Name",
                table: "BotChannelCategories",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BotChannels_TelegramId",
                table: "BotChannels",
                column: "TelegramId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BotChannels_Username",
                table: "BotChannels",
                column: "Username");

            migrationBuilder.CreateIndex(
                name: "IX_BotChannels_CategoryId",
                table: "BotChannels",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_BotChannelMembers_BotId_BotChannelId",
                table: "BotChannelMembers",
                columns: new[] { "BotId", "BotChannelId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BotChannelMembers_BotId",
                table: "BotChannelMembers",
                column: "BotId");

            migrationBuilder.CreateIndex(
                name: "IX_BotChannelMembers_BotChannelId",
                table: "BotChannelMembers",
                column: "BotChannelId");

            // 1) 迁移分类（去重：Name 全局唯一）
            migrationBuilder.Sql(@"
INSERT INTO BotChannelCategories (Name, Description, CreatedAt)
SELECT Name, MAX(Description), MIN(CreatedAt)
FROM BotChannelCategories_Old
GROUP BY Name;
");

            // 2) 迁移频道（TelegramId 全局唯一；取 SyncedAt 最新的一条作为全局信息）
            migrationBuilder.Sql(@"
INSERT INTO BotChannels (TelegramId, AccessHash, Title, Username, IsBroadcast, MemberCount, About, CreatedAt, SyncedAt, CategoryId)
SELECT bc.TelegramId, bc.AccessHash, bc.Title, bc.Username, bc.IsBroadcast, bc.MemberCount, bc.About, bc.CreatedAt, bc.SyncedAt, NULL
FROM BotChannels_Old bc
JOIN (
    SELECT TelegramId, MAX(SyncedAt) AS MaxSyncedAt
    FROM BotChannels_Old
    GROUP BY TelegramId
) m ON m.TelegramId = bc.TelegramId AND m.MaxSyncedAt = bc.SyncedAt
JOIN (
    SELECT TelegramId, SyncedAt, MAX(Id) AS MaxId
    FROM BotChannels_Old
    GROUP BY TelegramId, SyncedAt
) m2 ON m2.TelegramId = bc.TelegramId AND m2.SyncedAt = bc.SyncedAt AND m2.MaxId = bc.Id;
");

            // 3) 迁移 Bot-频道关系（同一 bot + 同一频道去重，SyncedAt 取最大）
            migrationBuilder.Sql(@"
INSERT INTO BotChannelMembers (BotId, BotChannelId, SyncedAt)
SELECT old.BotId, now.Id AS BotChannelId, MAX(old.SyncedAt) AS SyncedAt
FROM BotChannels_Old old
JOIN BotChannels now ON now.TelegramId = old.TelegramId
GROUP BY old.BotId, now.Id;
");

            // 4) 合并分类：同一 TelegramId 在多 bot 下若存在不同分类，取“出现次数最多”的分类（方案 A）
            migrationBuilder.Sql(@"
WITH CatNames AS (
    SELECT bc.TelegramId AS TelegramId, c.Name AS CatName
    FROM BotChannels_Old bc
    JOIN BotChannelCategories_Old c ON c.Id = bc.CategoryId
    WHERE bc.CategoryId IS NOT NULL
),
Counts AS (
    SELECT TelegramId, CatName, COUNT(*) AS Cnt
    FROM CatNames
    GROUP BY TelegramId, CatName
),
Pick AS (
    SELECT TelegramId, CatName
    FROM (
        SELECT TelegramId, CatName, Cnt,
               ROW_NUMBER() OVER (PARTITION BY TelegramId ORDER BY Cnt DESC, CatName ASC) AS rn
        FROM Counts
    )
    WHERE rn = 1
)
UPDATE BotChannels
SET CategoryId = (
    SELECT catNew.Id
    FROM Pick p
    JOIN BotChannelCategories catNew ON catNew.Name = p.CatName
    WHERE p.TelegramId = BotChannels.TelegramId
)
WHERE TelegramId IN (SELECT TelegramId FROM Pick);
");

            // 5) 清理旧表
            migrationBuilder.Sql("DROP TABLE BotChannels_Old;");
            migrationBuilder.Sql("DROP TABLE BotChannelCategories_Old;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 降级不做数据回填（不可逆）：仅恢复旧结构的空表，避免 Down 失败阻塞回滚链路。
            migrationBuilder.DropTable(name: "BotChannelMembers");
            migrationBuilder.DropTable(name: "BotChannels");
            migrationBuilder.DropTable(name: "BotChannelCategories");

            migrationBuilder.CreateTable(
                name: "BotChannelCategories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BotId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BotChannelCategories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BotChannelCategories_Bots_BotId",
                        column: x => x.BotId,
                        principalTable: "Bots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BotChannels",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BotId = table.Column<int>(type: "INTEGER", nullable: false),
                    TelegramId = table.Column<long>(type: "INTEGER", nullable: false),
                    AccessHash = table.Column<long>(type: "INTEGER", nullable: true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Username = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    IsBroadcast = table.Column<bool>(type: "INTEGER", nullable: false),
                    MemberCount = table.Column<int>(type: "INTEGER", nullable: false),
                    About = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SyncedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CategoryId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BotChannels", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BotChannels_Bots_BotId",
                        column: x => x.BotId,
                        principalTable: "Bots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BotChannels_BotChannelCategories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "BotChannelCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BotChannelCategories_BotId_Name",
                table: "BotChannelCategories",
                columns: new[] { "BotId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BotChannels_BotId_TelegramId",
                table: "BotChannels",
                columns: new[] { "BotId", "TelegramId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BotChannels_CategoryId",
                table: "BotChannels",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_BotChannels_Username",
                table: "BotChannels",
                column: "Username");
        }
    }
}
