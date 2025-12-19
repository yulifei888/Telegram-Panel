# 进阶说明

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

## 配置项速查

Docker 下常用环境变量（见 `docker-compose.yml`）：

- `ConnectionStrings__DefaultConnection`：SQLite 路径（默认 `/data/telegram-panel.db`）
- `Telegram__SessionsPath`：session 目录（默认 `/data/sessions`）
- `AdminAuth__CredentialsPath`：后台密码文件（默认 `/data/admin_auth.json`）
- `Sync__AutoSyncEnabled`：账号创建的频道/群组自动同步（默认关闭）
- `Telegram__BotAutoSyncEnabled`：Bot 频道轮询自动同步（默认关闭）
