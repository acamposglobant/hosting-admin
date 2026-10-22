# Hosting Admin — Deployment Guide (Increment 1)

Target: a single Ubuntu 24.04 VPS, read-only observability increment.
The panel is private: it must never be reachable without the identity-aware
proxy (IAP) in front of it.

> **Status:** this increment performs zero mutations. The deployment below has
> been validated by the automated suite (including Linux CI integration tests);
> the real IAP integration is validated only at first deployment — see
> [Residual risks](security/security-review-increment-1.md#logged-residual-risks).

## 1. Architecture of a deployment

```
Internet ──TLS──▶ IAP (MFA at the edge)
                     │
                     ▼
              Nginx :443 (TLS, serves SPA, proxies /api)
                     │  HTTP over loopback only
                     ▼
        hostingadmin-api.service (systemd, unprivileged)
                     │  127.0.0.1:5080
                     ▼
              SQLite (data/hostingadmin.db)
```

Controls in order of importance:

1. **Loopback binding (primary control).** The API listens only on
   `127.0.0.1:5080`. Nothing outside the host can reach it even if Nginx is
   misconfigured.
2. **IAP with MFA** authenticates the human at the edge (defense in depth
   layer 1). The IAP assertion is *not* an application identity — the
   application always requires its own session.
3. **Proxy attestation header** (defense in depth layer 2): Nginx sets a
   shared-secret header; the API rejects requests missing it when
   `Proxy:RequireAttestation` is enabled.
4. **Application auth**: password + server-side session cookie + CSRF token.

## 2. Prerequisites

- Ubuntu 24.04 (or compatible) with systemd.
- ASP.NET Core 10.0 runtime (`apt install dotnet-runtime-10.0` or the
  dotnet install script). The SDK is only needed on the build machine.
- Nginx (any current stable).
- TLS certificate for the panel hostname (e.g. via Let's Encrypt on the IAP
  or on Nginx, depending on where TLS terminates).
- A dedicated unprivileged user; the API never runs as root.

## 3. Build and publish

On a build machine (not the VPS):

```bash
# Frontend
cd frontend
npm ci
npm run build          # outputs frontend/dist

# Backend
dotnet publish src/HostingAdmin.Api -c Release -o publish/api
```

Copy to the VPS:

- `publish/api/*` → `/opt/hostingadmin/api/`
- `frontend/dist/*` → `/opt/hostingadmin/www/` (SPA static files)

## 4. Service account

```bash
sudo useradd --system --home /opt/hostingadmin --shell /usr/sbin/nologin hostingadmin
sudo mkdir -p /opt/hostingadmin/data
sudo chown -R hostingadmin:hostingadmin /opt/hostingadmin
sudo chmod 750 /opt/hostingadmin /opt/hostingadmin/data
```

The `data/` directory is the only location the service may write
(SQLite database, audit records). Everything else is read-only to it.

## 5. Production configuration

Create `/opt/hostingadmin/api/appsettings.Production.json` (mode 600, owned
by `hostingadmin`):

```json
{
  "Network": {
    "Urls": "http://127.0.0.1:5080"
  },
  "Database": {
    "Path": "/opt/hostingadmin/data/hostingadmin.db"
  },
  "Security": {
    "AllowedOrigins": [ "https://panel.example.invalid" ],
    "CsrfCookieName": "ha_csrf",
    "CsrfHeaderName": "X-CSRF-Token"
  },
  "Proxy": {
    "RequireAttestation": true,
    "AttestationHeaderName": "X-Proxy-Attestation",
    "AttestationSecret": "<generate: openssl rand -base64 32>",
    "TrustedProxyAddresses": [ "127.0.0.1", "::1" ]
  },
  "Services": {
    "UnitNames": [ "nginx.service", "ssh.service" ],
    "SystemctlPath": "/usr/bin/systemctl",
    "Timeout": "00:00:02"
  }
}
```

Notes:

- `Network:Urls` binds the API to loopback — this is the primary access
  control (plan D2). Do not bind a public interface.
- `AttestationSecret` is a secret: never commit it, never log it. The redaction
  denylist scrubs it from logs, but do not rely on that for new material.
- `AllowedOrigins` must list the exact origin browsers use; Origin validation
  rejects everything else.
- `Services:UnitNames` is the systemd allowlist — units not listed are never
  queried. Keep it minimal.

## 6. systemd unit

`/etc/systemd/system/hostingadmin-api.service`:

```ini
[Unit]
Description=Hosting Admin API (read-only observability)
After=network.target

[Service]
Type=exec
User=hostingadmin
Group=hostingadmin
WorkingDirectory=/opt/hostingadmin/api
ExecStart=/usr/bin/dotnet /opt/hostingadmin/api/HostingAdmin.Api.dll

Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=DOTNET_PRINT_TELEMETRY_MESSAGE=false

# Hardening: no privileges, no home, no /tmp sharing, minimal kernel surface
NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=true
PrivateTmp=true
PrivateDevices=true
ProtectKernelTunables=true
ProtectKernelModules=true
ProtectControlGroups=true
RestrictAddressFamilies=AF_UNIX AF_INET AF_INET6
RestrictNamespaces=true
LockPersonality=true
MemoryDenyWriteExecute=true
CapabilityBoundingSet=
AmbientCapabilities=
ReadWritePaths=/opt/hostingadmin/data

# Restart policy
Restart=on-failure
RestartSec=5

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now hostingadmin-api
sudo systemctl status hostingadmin-api
```

Verify it is listening on loopback only:

```bash
ss -tlnp | grep 5080   # must show 127.0.0.1:5080, not 0.0.0.0
```

## 7. Nginx reverse proxy

```nginx
server {
    listen 443 ssl http2;
    server_name panel.example.invalid;

    ssl_certificate     /etc/letsencrypt/live/panel.example.invalid/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/panel.example.invalid/privkey.pem;
    ssl_protocols TLSv1.2 TLSv1.3;

    # --- Header hygiene (plan D3) -------------------------------------
    # Overwrite (never append) client-supplied forwarding/identity headers.
    # The API trusts forwarded headers only from TrustedProxyAddresses;
    # identity is established ONLY by the application session cookie.
    proxy_set_header Host              $host;
    proxy_set_header X-Forwarded-For   $remote_addr;
    proxy_set_header X-Forwarded-Proto https;
    proxy_set_header X-Forwarded-User  "";
    proxy_set_header X-Proxy-Attestation "";

    # --- API ------------------------------------------------------------
    # Attestation secret is set HERE, by the proxy, and never accepted from
    # the client (the line above already cleared any client-supplied value).
    location /api/ {
        proxy_pass         http://127.0.0.1:5080;
        proxy_set_header   X-Proxy-Attestation "<same secret as appsettings>";
        proxy_read_timeout 30s;
    }

    # --- SPA static files ----------------------------------------------
    root /opt/hostingadmin/www;
    index index.html;

    location / {
        try_files $uri $uri/ /index.html;
    }

    # No caching of the SPA shell (session state changes)
    location = /index.html {
        add_header Cache-Control "no-store";
    }
}
```

Reload and verify:

```bash
sudo nginx -t && sudo systemctl reload nginx
curl -sS https://panel.example.invalid/api/health
```

### IAP placement

The IAP (identity-aware proxy providing MFA) sits in front of Nginx:

- Only the IAP may reach Nginx (firewall/security-group allowlist).
- The IAP performs MFA; its assertion is **not** an application identity —
  users still log in to the panel itself (defense in depth, plan D1).
- Nginx overwrites all client-supplied `X-Forwarded-*` and identity headers
  before proxying, so a malicious IAP client cannot spoof anything the API
  trusts.

## 8. First-admin bootstrap

Bootstrap is disabled by default; there is no public registration page.

1. On the VPS console (or over SSH from an allowed local address), activate
   the initialization window — the activation endpoint only accepts
   connections whose actual remote address is local (forwarded headers are
   never used for this decision):

   ```bash
   curl -sS -X POST http://127.0.0.1:5080/api/bootstrap/activate
   ```

2. Within the configured window (default 15 minutes), open the panel through
   the IAP and complete the administrator credential form. The password must
   meet the configured length policy (12–128 by default).

3. Completion is persisted; the window closes and cannot be reopened through
   the API. Re-activation requires local console access again.

All bootstrap attempts (activation, success, failure, post-completion) are
audited.

## 9. Updates and rollback

```bash
# Update
sudo systemctl stop hostingadmin-api
cp -a /opt/hostingadmin/api /opt/hostingadmin/api.bak-$(date +%F)
rsync -a --delete publish/api/ /opt/hostingadmin/api/
sudo systemctl start hostingadmin-api

# Rollback
sudo systemctl stop hostingadmin-api
rsync -a --delete /opt/hostingadmin/api.bak-YYYY-MM-DD/ /opt/hostingadmin/api/
sudo systemctl start hostingadmin-api
```

- The SQLite schema initializer is idempotent; rolling the binary back keeps
  the database intact.
- The frontend is static — redeploy by replacing `/opt/hostingadmin/www`.
- Never edit `appsettings.Production.json` while the service runs; stop,
  edit, start.

## 10. Post-deployment checklist

- [ ] `ss -tlnp` shows the API on `127.0.0.1` only.
- [ ] `curl https://panel.example.invalid/api/health` returns 200 through the IAP.
- [ ] Direct `curl http://<vps-ip>:5080/api/health` from outside fails (loopback only).
- [ ] Login works through the IAP; a failed login shows a generic error.
- [ ] A request without the attestation header (direct to Nginx, bypassing IAP
      rules) is rejected with 403 and audited.
- [ ] Dashboard polls every 10 s; audit screen pages and filters.
- [ ] Audit records show login success/failure and `audit.query` rows.
- [ ] `sudo systemctl status hostingadmin-api` shows no hardening violations
      (`systemd-analyze security hostingadmin-api` — review the exposure level).
- [ ] Secrets (attestation secret, credential hashes) appear nowhere in logs.
