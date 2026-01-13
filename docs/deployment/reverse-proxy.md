# 反向代理（Nginx/Caddy）

Telegram Panel 是 **Blazor Server**，需要 WebSocket（`/_blazor`）。

如果你反代后出现页面卡住/断开/一直重连，九成是 WebSocket 没配对。

另外，如果你遇到「打开页面被跳到 `http://localhost/login?ReturnUrl=%2F`」这种情况，说明反代没有把正确的 `Host`/`Proto` 透传给上游应用。

## Nginx（HTTP）示例

注意：请确保你的上游是 `http://127.0.0.1:5000`（对应 `docker-compose.yml` 暴露端口）。

```nginx
server {
  listen 80;
  server_name example.com;

  location / {
    proxy_pass http://127.0.0.1:5000;
    proxy_http_version 1.1;
    proxy_set_header Upgrade $http_upgrade;
    proxy_set_header Connection "Upgrade";
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-Host $host;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;

    proxy_read_timeout 3600;
    proxy_send_timeout 3600;
  }
}
```

## Caddy 示例

```caddy
example.com {
  reverse_proxy 127.0.0.1:5000
}
```

如果你使用了 CDN/面板（例如 Cloudflare），也要确认它对 WebSocket 的支持与超时设置。

## 宝塔面板（BT）反向代理要点

- 反代目标：`http://127.0.0.1:5000`
- 需要透传：`Host` + `X-Forwarded-Host` + `X-Forwarded-Proto`（否则可能跳到 `localhost`）

如果你要启用 Bot Webhook，还需要确保 Webhook 路径可外网访问：见 [Bot Webhook](bot-webhook.md)。
