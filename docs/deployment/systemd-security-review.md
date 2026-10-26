# systemd security review — first-deployment procedure (Increment 3, D28)

> **Status:** documented first-deployment gate. It can only run on a real host
> with the real units — CI cannot validate it. The gate is: the exposure level
> of both units is **recorded** in the first-deployment record table (§4), and
> every `EXPOSED` finding is either fixed or explicitly accepted in writing.
>
> Helper script: [`deploy/systemd-security-check.sh`](../../deploy/systemd-security-check.sh)

## 1. Why this is a deployment gate, not a CI gate

`systemd-analyze security` inspects the *installed* unit as systemd would run
it on this host: effective user/group, `ReadWritePaths` resolution, ambient
capabilities, `RestrictAddressFamilies` — several of these depend on the
kernel, filesystem, and unit file actually present on the target. A CI runner
has neither the units nor the host context, so a green CI build says nothing
about the on-host exposure. Making it a recorded deployment gate (D28) turns
"run it if someone remembers" into an explicit checklist item with a written
record.

## 2. Commands

Run on the VPS, after both units are installed and active:

```bash
systemd-analyze security hostingadmin-agent.service
systemd-analyze security hostingadmin-api.service
```

Or the helper, which runs both and prints a paste-ready summary:

```bash
sudo ./deploy/systemd-security-check.sh
```

### Reading the output

- The last line is the overall exposure, e.g.
  `→ Overall exposure level for hostingadmin-agent.service: 1.6 SAFE 🙂`
  or `… 9.3 EXPOSED 😨`.
- Above it, each hardening directive is listed with a weight:
  `✗` (missing, contributes to exposure), `✓` (set), `?` (unknown/not
  applicable on this host).
- Focus on `✗` lines for directives the units deliberately set
  (`ProtectSystem`, `PrivateTmp`, `CapabilityBoundingSet`, …). A `✗` there
  means the host kernel does not support it or the unit file drifted from the
  documented one — both are findings, not noise.

### Expected baseline

- **API unit** (`docs/deployment.md` §6): full hardening profile including
  `NoNewPrivileges=true`, `MemoryDenyWriteExecute=true`,
  `CapabilityBoundingSet=` — expected exposure **SAFE** (roughly ≤ 2.0).
- **Agent unit** (`docs/deployment/agent-systemd-unit.md`): same profile
  except `NoNewPrivileges` is intentionally omitted (sudo escalation requires
  it — see the unit doc §2). Expected exposure **SAFE-to-medium**; the
  documented, accepted deviation is exactly that one directive. Any other
  `EXPOSED` finding must be fixed or accepted in §4.

## 3. Acceptance criteria

The deployment passes this gate when **all** of the following hold:

1. Both analyses were run on the target host and their overall exposure
   levels are recorded in the table in §4.
2. Every `✗` finding on a directive the unit file sets is either:
   - **fixed** (unit file corrected, daemon-reload, re-run the analysis), or
   - **explicitly accepted in writing** in §4 with a one-line justification
     (e.g. "NoNewPrivileges omitted by design — D16 sudoers escalation, see
     agent unit doc §2").
3. Neither unit scores `EXPOSED` (≥ 9.0) after fixes/acceptances. If one does,
   the deployment is **not** complete: stop, fix, and re-run.
4. The record table is committed with the deployment notes (no secrets in it).

## 4. First-deployment record

> Fill this in during the first real deployment. One row per unit per
> deployment; add rows, never delete history.

| Date | Unit | Exposure level | Verdict (SAFE/…/EXPOSED) | EXPOSED findings — fixed or accepted (how) | Operator |
|------|------|----------------|--------------------------|--------------------------------------------|----------|
| _YYYY-MM-DD_ | `hostingadmin-api.service` | _e.g. 1.6_ | _SAFE_ | _none / accepted: …_ | _initials_ |
| _YYYY-MM-DD_ | `hostingadmin-agent.service` | _e.g. 2.0_ | _SAFE_ | _accepted: NoNewPrivileges omitted by design (D16 sudoers escalation, agent unit doc §2)_ | _initials_ |

## 5. Drift

Results are recorded at deployment time only; drift between deployments is a
known residual risk (plan §12). Re-run this review after **any** change to a
unit file, the sudoers artifact, or the host's systemd version, and add a new
row to §4.
