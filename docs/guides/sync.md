# “同步”功能说明

## 同步到底同步什么？

同步 = 从 Telegram 拉取并更新本地数据库中的“账号创建的数据”，主要用于**列表展示/筛选/分组/批量操作**：

- 账号创建的频道（Channel）
- 账号创建的群组（Group）

它不是：

- 手机号登录/收验证码
- 检测账号是否冻结/封禁
- 同步消息/聊天记录

## 为什么需要同步？

面板的频道/群组列表、分类、批量任务都依赖本地数据库。同步负责把 Telegram 侧的最新信息拉下来并落库。

## 自动同步是什么？

"自动同步"就是定时在后台执行同样的同步逻辑。

- 默认关闭：避免频繁调用 Telegram API。
- 开启后会写入本地覆盖配置（Docker 下在 `./docker-data/appsettings.local.json`）。

如果你不需要自动同步：保持关闭，平时用手动同步即可。

## Bot 频道同步

Bot 频道管理功能支持两种更新接收模式：

### 长轮询模式（默认）

- 后台服务定时调用 `getUpdates` 拉取更新
- 适合开发测试环境
- 配置：`Telegram__BotAutoSyncEnabled: "true"`

### Webhook 模式（生产推荐）

- Telegram 主动推送更新到你的服务器
- 更低资源消耗、更快响应速度
- 需要公网 HTTPS 地址
- 配置：
  - `Telegram__WebhookEnabled: "true"`
  - `Telegram__WebhookBaseUrl: "https://your-domain.com"`
  - `Telegram__WebhookSecretToken: "your-secret"`
  - `Telegram__BotAutoSyncEnabled: "true"`

详细配置说明见 [Bot Webhook](../deployment/bot-webhook.md)。

## 同步失败时的状态更新

当账号同步失败（如 Session 失效、账号被封禁等），系统会自动更新账号的 Telegram 状态：

- `TelegramStatusOk = false`
- `TelegramStatusSummary`：可读的错误摘要
- `TelegramStatusDetails`：详细错误信息

常见错误类型：
- `AUTH_KEY_UNREGISTERED`：Session 失效
- `AUTH_KEY_DUPLICATED`：Session 冲突（多设备使用同一 Session）
- `SESSION_REVOKED`：Session 已被撤销
- `PHONE_NUMBER_BANNED` / `USER_DEACTIVATED_BAN`：账号被封禁
