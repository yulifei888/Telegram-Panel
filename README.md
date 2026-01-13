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

## v1.30 重要更新

本次版本主要更新：

- ✨ 废号判定补全：`AUTH_KEY_DUPLICATED`（Session 冲突）与 `SESSION_REVOKED`（Session 已撤销）计入废号（影响“只看废号”筛选与清理逻辑）
- ✨ 一键清理筛选废号：账号页在勾选“只看废号”后提供“清理废号（筛选）”，按当前筛选结果直接批量清理
- ⚡ 自动同步调度优化：记录上次自动同步时间，避免重启即跑一轮导致限流
- ⚡ 批量任务配置落地：支持保存默认间隔/最大并发/重试开关，并默认更保守（降低风控风险）
- 🐛 日志降噪与限流降速：减少刷屏日志与高频请求
- 📚 新增文档站：更易维护与检索（https://moeacgx.github.io/Telegram-Panel/）

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

🐳 面向小白：`git clone` → `docker compose up` → 浏览器打开 → 登录改密码 → 配置 ApiId/ApiHash。

### 环境要求

Docker（Windows 推荐 Docker Desktop + WSL2；Linux 直接装 Docker Engine）

### 启动

```bash
git clone https://github.com/moeacgx/Telegram-Panel
cd Telegram-Panel
docker compose up -d --build
```

启动后访问：`http://localhost:5000`

### 默认后台账号（首次登录）

用户名：`admin`  
密码：`admin123`

登录后到「修改密码」页面改掉即可。

> 更完整的安装、更新、导入与生产部署建议：见 https://moeacgx.github.io/Telegram-Panel/ 。

## 截图

更多截图见：`screenshot/`

| | | |
|---|---|---|
| <img src="screenshot/Dashboard.png" width="300" /> | <img src="screenshot/account.png" width="300" /> | <img src="screenshot/Import account.png" width="300" /> |

## ⭐ Star History

[![Star History Chart](https://api.star-history.com/svg?repos=moeacgx/Telegram-Panel&type=Date)](https://star-history.com/#moeacgx/Telegram-Panel&Date)
