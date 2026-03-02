#!/usr/bin/env sh
set -eu

APP_ENTRY="TelegramPanel.Web.dll"
DEFAULT_APP_DIR="/app"
UPDATED_APP_DIR="/data/app-current"

mkdir -p /data /data/sessions /data/logs
if [ ! -f /data/appsettings.local.json ]; then
  printf '{}' > /data/appsettings.local.json
fi

APP_DIR="$DEFAULT_APP_DIR"
if [ -f "$UPDATED_APP_DIR/$APP_ENTRY" ]; then
  APP_DIR="$UPDATED_APP_DIR"
fi

# 运行目录下日志统一指向 /data/logs，避免更新目录轮换后日志丢失。
if [ -e "$APP_DIR/logs" ] && [ ! -L "$APP_DIR/logs" ]; then
  rm -rf "$APP_DIR/logs"
fi
if [ ! -e "$APP_DIR/logs" ]; then
  ln -s /data/logs "$APP_DIR/logs" || true
fi

# 面板保存的本地配置统一使用 /data/appsettings.local.json
if [ -e "$APP_DIR/appsettings.local.json" ] && [ ! -L "$APP_DIR/appsettings.local.json" ]; then
  rm -f "$APP_DIR/appsettings.local.json"
fi
if [ ! -e "$APP_DIR/appsettings.local.json" ]; then
  ln -s /data/appsettings.local.json "$APP_DIR/appsettings.local.json" || true
fi

cd "$APP_DIR"
exec dotnet "$APP_ENTRY"
