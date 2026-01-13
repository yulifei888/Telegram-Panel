# 更新升级（Docker 部署）

> 更新前建议先备份：`./docker-data/telegram-panel.db` 与 `./docker-data/`（尤其是重要账号的 sessions）。

在项目目录下执行：

```bash
git pull --rebase
docker compose up -d --build
```

说明：

- `docker compose up -d --build` 会重新构建并滚动更新容器（数据仍在 `./docker-data`，不会丢）
- 若你修改过 `.env` 或 `./docker-data/appsettings.local.json`，更新后也建议重启一次确保新配置生效

## 更新出错：git pull 提示本地修改会被覆盖

典型报错：

```
error: Your local changes to the following files would be overwritten by merge:
        docker-compose.yml
Please commit your changes or stash them before you merge.
Aborting
```

原因：你本地改过 `docker-compose.yml`，导致更新时 Git 不允许直接覆盖。

推荐做法：尽量不要直接改 `docker-compose.yml`：

- Webhook 等部署差异：用 `.env`（参考 `.env.example`）
- 功能开关/参数：用面板「系统设置」保存到 `./docker-data/appsettings.local.json`（见 [配置与数据目录](../reference/configuration.md)）

处理方式（二选一）：

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
