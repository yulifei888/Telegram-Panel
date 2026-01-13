# 安装部署

本文档用于把 Telegram Panel 跑起来（推荐 Docker）。

## Docker 部署（推荐）

### 环境要求

- Docker（Windows 推荐 Docker Desktop + WSL2；Linux 直接安装 Docker Engine）

### 启动

```bash
git clone https://github.com/moeacgx/Telegram-Panel
cd Telegram-Panel
docker compose up -d --build
```

启动后访问：`http://localhost:5000`

### 默认后台账号（首次登录）

- 用户名：`admin`
- 密码：`admin123`

登录后到「修改密码」页面改掉即可。

### 必做配置：Telegram API 凭据

到 https://my.telegram.org/apps 用任意一个 Telegram 账号申请一次 `api_id` / `api_hash`，然后在面板「系统设置」里保存即可。

> 说明：不需要每个账号都申请，全站共用这一对即可工作。  
> 注意：`api_hash` 是 **32 位十六进制字符串（0-9a-f）**，请不要填 Token/用户名/URL 等其它值。

### 数据持久化（别乱删）

容器内所有持久化数据统一挂载到宿主机 `./docker-data`：

- 数据库：`./docker-data/telegram-panel.db`
- Sessions：`./docker-data/sessions/`
- 系统设置本地覆盖：`./docker-data/appsettings.local.json`
- 后台登录凭据文件：`./docker-data/admin_auth.json`

更多说明见：[配置与数据目录](../reference/configuration.md)。

## 生产部署入口

- 反向代理（含 WebSocket）：见 [反向代理](../deployment/reverse-proxy.md)
- Bot Webhook（可选，生产推荐）：见 [Bot Webhook](../deployment/bot-webhook.md)

## 更新与常见问题

- 更新升级：见 [更新升级](update.md)
- 常见问题：见 [常见问题](faq.md)

## 下一步

- 账号导入：见 [账号导入（压缩包）](../guides/account-import.md)
- 新号必看：见 [防冻结指南](../guides/anti-freeze.md)
- 列表/批量能力依赖同步：见 [同步说明](../guides/sync.md)

## 本地开发运行（可选）

```bash
dotnet run --project src/TelegramPanel.Web
```
