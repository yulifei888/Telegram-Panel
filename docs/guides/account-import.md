# 账号导入（Zip / TData）

当前支持两种压缩包导入格式：

- Telethon 格式：`.json + .session`
- TData 格式：`tdata` 目录（含 `key_datas` / `D877F783D5D3EF8C*`）

## 方式一：Telethon 压缩包（推荐）

### 单账号结构

```
account.zip
  ├─ 8613111111111.json
  ├─ 8613111111111.session
  └─ 2fa.txt            # 可选，内容为二级密码
```

### 批量结构

```
accounts.zip
  ├─ 8613111111111
  │   ├─ 8613111111111.json
  │   ├─ 8613111111111.session
  │   └─ 2fa.txt
  └─ 8615119714541
      ├─ 8615119714541.json
      └─ 8615119714541.session
```

规则说明：

- 每个账号目录内只要能找到 `1 个 .json + 1 个 .session` 即可导入
- `2fa.txt` 为可选；若存在则会优先作为该账号二级密码
- 目录名、文件名建议使用手机号，便于排查

## 方式二：TData 压缩包

支持 Zip 内包含 `tdata` 目录（可单账号，也可批量多目录）。

示例（单账号）：

```
tdata-account.zip
  └─ tdata
      ├─ key_datas
      ├─ D877F783D5D3EF8C
      └─ ...
```

示例（批量）：

```
tdata-accounts.zip
  ├─ acc-a
  │   └─ tdata
  │       ├─ key_datas
  │       └─ D877F783D5D3EF8C
  └─ acc-b
      └─ tdata
          ├─ key_datas
          └─ D877F783D5D3EF8C
```

注意：

- 导入 TData 前，需先在「系统设置」配置全局 Telegram API（`ApiId/ApiHash`）
- 首次导入 TData 时会自动准备解析依赖，耗时会比普通导入长一点

## Docker 部署下导入文件存储位置

导入成功后，会话文件统一写入：

- `./docker-data/sessions/`

不要手工改名或删除该目录中的文件，避免账号会话失效。
