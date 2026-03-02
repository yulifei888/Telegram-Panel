# 更新升级（Docker 部署）

> 更新前建议先备份：`./docker-data/telegram-panel.db` 与 `./docker-data/`（尤其是重要账号的 sessions）。

## 方式一：面板内一键更新（推荐先用）

入口：`左上角版本号 -> 版本信息弹窗 -> 一键更新并重启`

说明：

- 该方式基于 GitHub Release 更新包（`linux-x64/linux-arm64 zip`）
- 会自动匹配架构并部署到 `/data/app-current`
- 适合快速更新业务版本（无需手动执行命令）

## 方式二：更新 Docker 镜像（建议定期执行）

在项目目录下执行：

```bash
docker compose pull
docker compose up -d
```

适用场景：

- 更新基础镜像层（运行时/系统依赖/安全补丁）
- `.env` 的 `TP_IMAGE` 改为新 tag 后切换到指定镜像版本

## 常见现象：镜像更新了，页面还是旧版

先检查当前程序实际运行目录：

```bash
docker exec telegram-panel sh -lc 'readlink /proc/1/cwd'
```

如果输出是 `/data/app-current`，说明当前在运行「面板一键更新」落地的版本，而不是镜像内 `/app` 版本。

### 切回“手动镜像更新”模式（推荐）

```bash
cd /home/docker/Telegram-Panel

docker compose down
mv docker-data/app-current docker-data/app-current.bak-$(date +%s)

docker compose pull
docker compose up -d --force-recreate
```

再次确认：

```bash
docker exec telegram-panel sh -lc 'readlink /proc/1/cwd'
```

应输出 `/app`。

## 远程镜像 与 本地构建：如何切换

### A. 远程镜像 -> 本地构建镜像

1. 把 `.env` 里的镜像改为本地标签（示例）：

```bash
TP_IMAGE=telegram-panel:local
```

2. 在项目根目录构建本地镜像：

```bash
docker build -t telegram-panel:local .
```

3. 以本地镜像重建容器（避免拉取远端）：

```bash
docker compose up -d --pull never --force-recreate
```

### B. 本地构建镜像 -> 远程镜像（latest/dev-latest/tag）

1. 把 `.env` 里的 `TP_IMAGE` 改回 GHCR 镜像，例如：

```bash
TP_IMAGE=ghcr.io/moeacgx/telegram-panel:dev-latest
```

2. 拉取并重建：

```bash
docker compose pull
docker compose up -d --force-recreate
```

### C. 校验当前容器到底跑的是哪个镜像

```bash
docker inspect telegram-panel --format '{{.Config.Image}}'
docker exec telegram-panel sh -lc 'readlink /proc/1/cwd'
```

## 从源码部署的用户（可选）

如果你不是用 GHCR 远程镜像，而是本地构建镜像部署，可使用：

```bash
git pull --rebase
docker compose up -d --build
```

## 更新出错：`git pull` 提示本地修改会被覆盖

典型报错：

```
error: Your local changes to the following files would be overwritten by merge:
        docker-compose.yml
Please commit your changes or stash them before you merge.
Aborting
```

原因：你本地改过 `docker-compose.yml`，导致更新时 Git 不允许直接覆盖（仅源码更新路径会遇到）。

推荐做法：尽量不要直接改 `docker-compose.yml`：

- Webhook 等部署差异：用 `.env`（参考 `.env.example`）
- 功能开关/参数：用面板「系统设置」保存到 `./docker-data/appsettings.local.json`（见 [配置与数据目录](../reference/configuration.md)）

处理方式（二选一）：

1) 放弃本地修改（最快、推荐）

```bash
git restore docker-compose.yml
git pull --rebase
docker compose up -d
```

2) 保留本地修改（自己承担后续合并成本）

```bash
git stash push -m "local docker-compose" -- docker-compose.yml
git pull --rebase
git stash pop
docker compose up -d
```

如果 `git stash pop` 出现冲突，按提示手动合并 `docker-compose.yml` 后再继续。
