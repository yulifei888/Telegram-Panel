# 配置与数据目录

## 技术栈

- .NET 8 / ASP.NET Core / Blazor Server
- MudBlazor
- EF Core（默认 SQLite）
- WTelegramClient（MTProto）

## Docker 数据目录（强相关）

`docker-compose.yml` 会把宿主机 `./docker-data` 挂载到容器 `/data`，核心文件包括：

- `/data/telegram-panel.db`：SQLite 数据库
- `/data/sessions/`：账号 session 文件
- `/data/appsettings.local.json`：UI 保存后的本地覆盖配置
- `/data/admin_auth.json`：后台登录账号/密码（首次会用初始默认值生成）

## 后台任务（刷新页面不影响）

部分批量任务会在后台静默执行（避免“刷新页面就中断”）：

- 批量邀请
- 批量设置管理员

## 账号状态检测（深度探测）

为更可靠识别冻结/受限等状态，支持深度探测（例如通过创建/删除测试频道来探测权限）。

检测结果会持久化到数据库，避免刷新页面又变回“未检测”。

## 清理废号（封禁/受限/未登录/session 失效）

在「账号列表」与「外置验证码链接」页面支持“清理废号”（多选批量）：

- 会先执行 Telegram 状态检测（可选普通/深度）
- 仅当判定为废号（封禁/受限/被冻结/需要 2FA/Session 失效或损坏）才会删除
- 删除范围：数据库记录 + `*.session`（含常见备份/同名 json）
- 若遇到 `*.session` 文件被占用，会先尝试从 `TelegramClientPool` 释放客户端并重试删除

另外，系统「账号列表」支持“一键清理所有废号”（扫描系统全部账号）。

## 配置项速查

Docker 下常用环境变量（见 `docker-compose.yml`）：

- `ConnectionStrings__DefaultConnection`：SQLite 路径（默认 `/data/telegram-panel.db`）
- `Telegram__SessionsPath`：session 目录（默认 `/data/sessions`）
- `AdminAuth__CredentialsPath`：后台密码文件（默认 `/data/admin_auth.json`）
- `Sync__AutoSyncEnabled`：账号创建的频道/群组自动同步（默认关闭）
- `Telegram__BotAutoSyncEnabled`：Bot 频道自动同步（默认关闭）
- `Telegram__WebhookEnabled`：Bot Webhook 模式开关（默认关闭，使用长轮询）
- `Telegram__WebhookBaseUrl`：Webhook 公网 HTTPS 地址
- `Telegram__WebhookSecretToken`：Webhook 验证密钥

## UI 保存到本地覆盖配置

面板里的部分“保存”按钮会把设置写入 `appsettings.local.json`（Docker 下为 `/data/appsettings.local.json`），常见键：

- `Telegram:BotAutoSyncEnabled` / `Telegram:BotAutoSyncIntervalSeconds`：Bot 频道后台自动同步轮询开关/间隔
- `ChannelAdminDefaults:Rights`：批量设置管理员的“默认权限”
- `ChannelAdminPresets:Presets`：批量设置管理员的“用户名列表预设”（名称 -> usernames）
- `ChannelInvitePresets:Presets`：批量邀请成员的“用户名列表预设”（名称 -> usernames）

## Bot 启用/停用（每个 Bot）

机器人管理页可以对单个 Bot 启用/停用：停用后该 Bot 不会再被后台轮询 `getUpdates`，也不会被需要 Bot 的模块/任务使用。

## Bot Webhook 模式（生产环境推荐）

Bot Webhook 的完整配置与注意事项已单独整理：见 [Bot Webhook](../deployment/bot-webhook.md)。
