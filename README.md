# Telegram Panel

基于 **WTelegramClient** 的 Telegram 多账户管理面板（.NET 8 / Blazor Server）。

<p align="center">
  <img src="https://img.shields.io/badge/.NET-8.0-512BD4?style=for-the-badge&logo=dotnet&logoColor=white" alt=".NET 8.0">
  <img src="https://img.shields.io/badge/Blazor-Server-512BD4?style=for-the-badge&logo=blazor&logoColor=white" alt="Blazor Server">
  <img src="https://img.shields.io/badge/Docker-Compose-2496ED?style=for-the-badge&logo=docker&logoColor=white" alt="Docker Compose">
  <img src="https://img.shields.io/badge/Powered%20by-WTelegramClient-333333?style=for-the-badge" alt="Powered by WTelegramClient">
</p>

<p align="center">
  📚 <b><a href="https://moeacgx.github.io/Telegram-Panel/">文档站</a></b> |
  🏪 <b><a href="https://faka.boxmoe.eu.org/">API 账号购买</a></b> |
  🖼️ <b><a href="screenshot/">截图</a></b> |
  💬 <b><a href="https://t.me/zhanzhangck">TG 频道</a></b> |
  👥 <b><a href="https://t.me/vpsbbq">站长交流群</a></b>
</p>

## v1.31 重要更新

本次版本主要更新：

- ✨ TData 导入完善：支持完整导入 tdata 压缩包，并补全导入链路会话校验，减少导入后不可用情况
- ✨ TData 导出优化：支持 Telethon/TData 双格式导出；修复官方客户端登录兼容问题；优先已授权 DCSession；补齐 `user_id` 写入；修复旧包缓存导致重复下载
- ✨ Bot 频道异常治理：支持删除失效频道、频道状态检测（正常/异常）和异常筛选，降低被踢/封禁后脏数据残留
- ✨ Docker 一键更新：支持面板内检测 Release 并一键更新，自动下载匹配架构包、部署到 `/data/app-current` 并重启生效
- ⚡ 更新入口统一：左上角版本弹窗集成自动检测、更新确认与 Latest Release Notes 展示（移除设置页重复入口）
- ⚡ CI/CD 完整化：新增 Docker 自动构建推送（GHCR）与 Release 自动发布/自动 changelog/更新包资产上传
- 📚 部署文档重排：Docker 部署流程精简为 compose 单路径（稳定版/开发版切换更直观）

## 功能概览

- 📥 多账号批量导入/登录：压缩包导入；手机号验证码登录；2FA 密码
- 👥 批量运营能力：批量加群/订阅/退群、批量邀请成员/机器人、批量设置管理员、导出链接等
- 📱 一键踢出其他设备：保留面板当前会话，清理其它在线设备
- 🧹 废号检测与一键清理：封禁/受限/冻结/未登录/Session 失效等状态批量处理
- 🔐 2FA 管理：单个/批量修改二级密码；绑定/换绑找回邮箱（支持对接 Cloud Mail 自动收码确认）
- 🧩 模块化扩展：任务 / API / UI 可安装扩展（见 `docs/developer/modules.md`）

## TODO（规划）

- [ ] 一键退群/退订、订阅（频道/群组）
- [ ] 一键清空联系人
- [ ] 批量手机号验证码重新登录（用于刷新会话 session）
- [ ] 手机号注册：未注册号支持完整注册流程（姓名/可选邮箱/邮箱验证码等）
- [ ] 通用接码 API：抽象接口 + 主程序只依赖抽象；厂商通过“适配模块”对接（无需改动主程序代码）
- [ ] 支持更换手机号
- [ ] 多代理：支持账号分类绑定代理
- [ ] 多 API：支持账号分类绑定 ApiId/ApiHash
- [ ] 定时创建频道、定时公开频道
- [ ] 定时刷粉丝：对接刷粉 API（通用适配结构），通过适配模块对接多家刷粉平台
- [ ] 群聊定时发言养号

## 快速开始

### Docker 一键部署（推荐）

环境要求：Docker（Windows 推荐 Docker Desktop + WSL2；Linux 直接装 Docker Engine）

#### 第一步：准备项目

```bash
git clone https://github.com/moeacgx/Telegram-Panel
cd Telegram-Panel
cp .env.example .env
```

#### 第二步：选择镜像版本

默认是稳定版（无需改动）：

```bash
TP_IMAGE=ghcr.io/moeacgx/telegram-panel:latest
```

如果你要开发版，改 `.env` 为：

```bash
TP_IMAGE=ghcr.io/moeacgx/telegram-panel:dev-latest
```

#### 第三步：启动

```bash
docker compose pull
docker compose up -d
```

访问：`http://localhost:5000`

#### 默认后台账号（首次登录）

用户名：`admin`  
密码：`admin123`

登录后到「修改密码」页面改掉即可。

#### 常用命令

```bash
# 查看日志
docker compose logs -f

# 更新到当前 .env 指定的镜像版本
docker compose pull
docker compose up -d

# 重启 / 停止
docker compose restart
docker compose down
```

## Docker 一键更新（面板内）

面板已支持在 Docker 部署场景下一键更新（左上角版本号 -> 版本信息弹窗）：

1. 点击“检查更新”，读取 GitHub 最新 Release。
2. 点击“一键更新并重启”，自动下载对应架构的 Linux 更新包到 `/data/app-current`。
3. 程序触发重启后，容器会优先从 `/data/app-current` 启动新版本（无需手动 `docker compose pull`）。

说明：
- 当前仅支持 Docker 容器内执行一键更新。
- 更新资产依赖 `release.yml` 工作流产物；若 Release 没有 `linux-x64/linux-arm64` zip 资产，则一键更新会提示不可用。

## 截图

更多截图见：`screenshot/`

| | | |
|---|---|---|
| <img src="screenshot/Dashboard.png" width="300" /> | <img src="screenshot/account.png" width="300" /> | <img src="screenshot/Import account.png" width="300" /> |

## ⭐ Star History

[![Star History Chart](https://api.star-history.com/svg?repos=moeacgx/Telegram-Panel&type=Date)](https://star-history.com/#moeacgx/Telegram-Panel&Date)
