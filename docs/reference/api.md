# 接口速查（简版）

本页用于二次开发/排查问题时快速定位接口；完整行为以代码为准。

## 账号

- `GET /api/accounts`：获取账号列表
- `GET /api/accounts/{id}`：获取账号详情
- `POST /api/accounts/login`：手机号登录（发送验证码）
- `POST /api/accounts/verify`：提交验证码/2FA
- `POST /api/accounts/import`：导入账号（Session/压缩包）
- `POST /api/accounts/{id}/sync`：同步该账号“创建的频道/群组”
- `DELETE /api/accounts/{id}`：删除账号

## 频道/群组

- `GET /api/channels`：频道列表（筛选）
- `GET /api/channels/{id}`：频道详情
- `POST /api/channels/{id}/admins`：设置管理员
- `POST /api/channels/{id}/invite`：邀请用户/Bot

## 任务

- `GET /api/tasks`：任务列表
- `GET /api/tasks/{id}`：任务详情
- `POST /api/tasks/{id}/cancel`：取消
- `POST /api/tasks/{id}/retry`：重试
