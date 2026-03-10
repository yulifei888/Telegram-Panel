# 文档维护

本项目使用 **MkDocs Material** 生成文档站，文档源文件统一放在 `docs/`。

## 本地预览

使用 `uv`（推荐）：

```bash
uv venv
uv pip install -r requirements-docs.txt
uv run mkdocs serve
```

生成静态站点：

```bash
uv run mkdocs build
```

## 目录约定（面向使用者优先）

- `docs/getting-started/`：从 0 到可用（安装、升级、FAQ）
- `docs/guides/`：日常使用与操作指南
- `docs/deployment/`：反向代理、Webhook、生产运维相关
- `docs/reference/`：配置/数据库/API 等参考型内容
- `docs/developer/`：模块开发与维护者说明

## 新增/移动页面的规则

- 新页面：直接在对应目录新增 `*.md`
- 侧边栏与顺序：在 `mkdocs.yml` 的 `nav:` 中维护
- 链接：尽量使用相对路径链接（例如 `../guides/sync.md`），避免写死仓库 URL

## GitHub Pages 发布

已内置工作流：`.github/workflows/docs.yml`。

启用方式（只需要做一次）：

1) 仓库 Settings → Pages
2) Source 选择 **GitHub Actions**

之后每次合并到 `main`（且改动命中 `docs/**`/`mkdocs.yml` 等）会自动构建并发布。
