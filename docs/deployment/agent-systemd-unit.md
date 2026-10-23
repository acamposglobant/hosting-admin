# Hosting Admin Agent — systemd unit draft (Increment 2, task 1)

> Status: **draft** — finalized in task 7 with the full deployment checklist.
> This file documents the planned unit and the `NoNewPrivileges` analysis
> required by plan task 1 (§3.2, §9 task 1).

## 1. Unit draft

`/etc/systemd/system/hostingadmin-agent.service`:

```ini
[Unit]
Description=Hosting Admin privileged agent (allowlisted service actions)
# The socket directory is created by tmpfiles.d (see §3); the agent binds
# /run/hostingadmin/agent.sock at startup.
After=network.target

[Service]
Type=exec
# D16: the agent runs under its OWN unprivileged account — never root,
# never the API's hostingadmin account.
User=hostingadmin-agent
Group=hostingadmin-agent
WorkingDirectory=/opt/hostingadmin/agent
ExecStart=/usr/bin/dotnet /opt/hostingadmin/agent/HostingAdmin.Agent.dll

Environment=DOTNET_ENVIRONMENT=Production
Environment=DOTNET_PRINT_TELEMETRY_MESSAGE=false

# Hardening: same profile as the API unit (docs/deployment.md §6).
NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=true
PrivateTmp=true
PrivateDevices=true
ProtectKernelTunables=true
ProtectKernelModules=true
ProtectControlGroups=true
RestrictAddressFamilies=AF_UNIX
RestrictNamespaces=true
LockPersonality=true
MemoryDenyWriteExecute=true
CapabilityBoundingSet=
AmbientCapabilities=
# The agent needs no writable paths at runtime (config and secret are read
# at startup; the socket lives in the tmpfiles-managed directory).
ReadWritePaths=

# Restart policy
Restart=on-failure
RestartSec=5

[Install]
WantedBy=multi-user.target
```

Socket directory via tmpfiles (`/etc/tmpfiles.d/hostingadmin-agent.conf`):

```conf
# Type Path                    Mode User                 Group                Age Argument
d      /run/hostingadmin       0750 root                 hostingadmin-agent   -
```

The agent creates `agent.sock` inside this root-owned directory. Socket
permissions (mode 660, shared group per D17) are enforced by the agent at
startup in task 3; the directory mode is the primary access control.

## 2. NoNewPrivileges vs sudo — verification result

Plan §3.2 assumed `NoNewPrivileges=true` on the agent unit is compatible with
escalation via `sudo` (a setuid binary). **That assumption is incorrect.**

`NoNewPrivileges=true` sets the kernel-wide `NO_NEW_PRIVS` flag for the
service. With it set, the kernel **ignores the setuid/setgid bits on exec** —
`sudo` therefore cannot elevate and fails with an authentication/permission
error. This is documented kernel/systemd behavior, not a deployment quirk:

- `man 2 prctl` (PR_SET_NO_NEW_PRIVS): "the execve of a setuid/setgid binary
  will not result in elevated privileges".
- `man 7 daemon` / systemd.exec(5): "the process and all its children can never
  gain new privileges via execve(2) — setuid/setgid bits ... have no effect".

**Consequence for the design (D16):** the agent unit must NOT set
`NoNewPrivileges=true` if escalation is via sudoers. The escalation surface is
still bounded exactly as planned — sudoers enumerates complete fixed command
lines only (2 × N lines, `NOPASSWD`) — but the hardening profile drops that one
directive and compensates with the rest of the profile plus the enumerated
sudoers rule.

**Alternatives evaluated (decision recorded for task 7):**

| Option | `NoNewPrivileges` | Assessment |
|---|---|---|
| sudoers with enumerated command lines (D16 default) | must be `false` | Simple, auditable; loses one hardening directive on the agent unit only. The API unit keeps `NoNewPrivileges=true` unchanged. |
| polkit rule + `systemd-run`/D-Bus call | can stay `true` | No setuid needed (polkit authorizes via D-Bus to systemd), but adds a D-Bus surface the plan explicitly chose to avoid (D16). |
| `systemd-run` scoped to the agent's own unit | n/a | Does not grant root; not applicable. |

**Chosen:** keep D16 (sudoers) with `NoNewPrivileges` omitted from the agent
unit. This is a deliberate, documented deviation from the plan's §3.2
expectation, to be confirmed in the task 7 security review. If the review
rejects it, the fallback is the polkit alternative (D16 already documents it).

**On-target verification (task 7 checklist, operator-run):**

```bash
# After install: confirm the agent cannot elevate beyond the enumerated lines
sudo -u hostingadmin-agent systemctl restart <allowlisted-unit>   # must FAIL (no sudoers entry for direct call)
sudo -u hostingadmin-agent sudo systemctl restart <allowlisted-unit>  # must SUCCEED (enumerated line)
sudo -u hostingadmin-agent sudo systemctl restart other.service   # must FAIL
systemd-analyze security hostingadmin-agent   # review exposure level
```

## 3. Sudoers policy (preview — full artifact in task 7)

```sudoers
# /etc/sudoers.d/hostingadmin-agent — managed file, exact lines only.
# One line per allowlisted unit × action; no wildcards, no variables.
hostingadmin-agent ALL=(root) NOPASSWD: /usr/bin/systemctl start nginx.service
hostingadmin-agent ALL=(root) NOPASSWD: /usr/bin/systemctl stop nginx.service
hostingadmin-agent ALL=(root) NOPASSWD: /usr/bin/systemctl restart nginx.service
```

Every line is a complete fixed command line: the agent cannot express "any
unit" or extra arguments, and a compromised agent account can run exactly
these commands and nothing more (D16, plan §3.2).
