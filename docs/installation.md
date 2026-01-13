# 安装部署

本文档用于把 Telegram Panel 跑起来（推荐 Docker）。README 只保留简洁入口信息。

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

## 更新方法（Docker 部署）

> 更新前建议先备份：`./docker-data/telegram-panel.db` 与 `./docker-data/`（尤其是重要账号的 sessions）。

在项目目录下执行：

```bash
git pull --rebase
docker compose up -d --build
```

说明：

- `docker compose up -d --build` 会重新构建并滚动更新容器（数据仍在 `./docker-data`，不会丢）
- 若你修改过 `.env` 或 `./docker-data/appsettings.local.json`，更新后也建议重启一次确保新配置生效

### 更新出错：git pull 提示本地修改会被覆盖

典型报错：

```
error: Your local changes to the following files would be overwritten by merge:
        docker-compose.yml
Please commit your changes or stash them before you merge.
Aborting
```

原因：你本地改过 `docker-compose.yml`（很常见：顺手把某个开关写死在 compose 里），导致更新时 Git 不允许直接覆盖。

推荐做法：尽量不要直接改 `docker-compose.yml`：

- Webhook 等部署差异：用 `.env`（参考 `.env.example`）
- 功能开关/参数：用面板「系统设置」保存到 `./docker-data/appsettings.local.json`

处理方式（两选一）：

1) 放弃本地修改（最快、推荐）

```bash
git restore docker-compose.yml
git pull --rebase
docker compose up -d --build
```

2) 保留本地修改（自己承担后续合并成本）

```bash
git stash push -m "local docker-compose" -- docker-compose.yml
git pull --rebase
git stash pop
docker compose up -d --build
```

如果 `git stash pop` 出现冲突，按提示手动合并 `docker-compose.yml` 后再继续。

### 反向代理（生产环境建议）

如果你用宝塔面板（Nginx）部署，通常**直接用默认反向代理**到面板端口即可：

- 目标 URL：`http://127.0.0.1:5000`（或你 compose 暴露的端口）
- 记得在宝塔反代设置里开启/勾选 **WebSocket**（Blazor Server 需要）
- HTTPS 建议在宝塔侧配置证书

更完整的 Nginx/Caddy 配置参考：`docs/reverse-proxy.md`。

### Bot Webhook（可选）

如果你使用 Bot 相关功能并希望用 Webhook（而非长轮询），请先确保：

- 已配置反向代理并能外网访问（HTTPS 强烈建议）
- 面板访问域名稳定，且能被 Telegram 访问

Webhook 的具体配置项、`.env`/`appsettings.local.json` 覆盖方式请参考 `docs/advanced.md`。

## 其它文档入口

- 反向代理：`docs/reverse-proxy.md`
- 配置项/环境变量/后台任务：`docs/advanced.md`
- 同步说明：`docs/sync.md`
- API 速查：`docs/api.md`
- 数据库结构：`docs/database.md`
- 模块系统：`docs/modules.md`
- 防冻结指南：`docs/anti-freeze.md`

## 本地开发运行（可选）

```bash
dotnet run --project src/TelegramPanel.Web
```

## 常见问题

### 发送验证码提示“session 被占用/进程占用”

- 确保不要多开多个面板实例共享同一个 `sessions` 目录（会互相锁文件）
- 如果刚修改了全局 `ApiId/ApiHash`，建议重启面板再登录
