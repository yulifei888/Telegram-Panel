# Bot Webhook（生产环境推荐）

> 如果你不使用「Bot 频道管理」功能，可以跳过本页。

默认情况下，Bot 使用**长轮询（Long Polling）**模式接收更新。生产环境建议使用 Webhook 模式，优势如下：

- 更低的资源消耗（无需持续轮询）
- 更快的响应速度（Telegram 主动推送）
- 更适合高流量/多 Bot 场景

前置条件：

- 需要公网可访问地址（强烈建议 HTTPS）
- 需要反向代理正确配置 WebSocket/转发头（见 [反向代理](reverse-proxy.md)）

## Webhook 配置项

在 `docker-compose.yml` 或 `appsettings.local.json` 中配置：

| 配置项 | 环境变量 | 说明 |
|--------|----------|------|
| `Telegram:WebhookEnabled` | `Telegram__WebhookEnabled` | 设为 `true` 启用 Webhook 模式；默认 `false` 使用轮询 |
| `Telegram:WebhookBaseUrl` | `Telegram__WebhookBaseUrl` | 你的公网 HTTPS 地址（Telegram 要求必须 HTTPS） |
| `Telegram:WebhookSecretToken` | `Telegram__WebhookSecretToken` | 验证密钥，Telegram 会在请求头中携带此值供校验 |
| `Telegram:BotAutoSyncEnabled` | `Telegram__BotAutoSyncEnabled` | 设为 `true` 启用自动同步；Bot 加入新频道后自动添加到列表 |

## docker-compose.yml 配置示例

```yaml
environment:
  Telegram__WebhookEnabled: "true"
  Telegram__WebhookBaseUrl: "https://your-domain.com"
  Telegram__WebhookSecretToken: "your-random-secret-token"
  Telegram__BotAutoSyncEnabled: "true"
```

## 注意事项

- Webhook 模式必须使用 HTTPS，Telegram 不支持 HTTP
- 启用 Webhook 后，系统会在启动时为所有活跃 Bot 注册 Webhook
- 如果你使用反向代理，确保 `/api/bot/webhook/*` 路径可以被外部访问
- 同一个 Bot Token 同时只能使用一种模式（Webhook 或 Long Polling），切换模式会自动覆盖
- 切换模式后需要重启服务生效

## 反向代理配置（Webhook 路径）

Nginx 示例：

```nginx
location /api/bot/webhook/ {
  proxy_pass http://127.0.0.1:5000;
  proxy_http_version 1.1;
  proxy_set_header Host $host;
  proxy_set_header X-Real-IP $remote_addr;
  proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
  proxy_set_header X-Forwarded-Proto $scheme;
}
```
