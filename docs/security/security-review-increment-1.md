# Security Review — Increment 1 (Read-Only Observability)

Review pass performed at plan task 16, covering tasks 1–15.
Method: control-by-control review against the plan's decisions (D1–D14) and
the threat model, with test evidence from the automated suite
(181 unique test cases; 209 .NET tests + 43 frontend tests green).

## Verdict

**No open findings blocking deployment.** All planned controls are implemented
and tested. Residual risks are logged below and are acceptable for a private,
single-admin, read-only increment — none claim protection the code does not
have.

## Controls verified

| # | Control (plan ref) | Implementation | Test evidence (selection) |
|---|---|---|---|
| 1 | Loopback binding as primary control (D2) | `Network:Urls` → `127.0.0.1` in production config; deployment doc makes it the first checklist item | Deployment checklist; binding is config-validated at startup |
| 2 | IAP is layer 1, app auth is layer 2 (D1) | Own credential + session; IAP assertion never used as identity | `Authenticate_*`, `Login_*` suites |
| 3 | Proxy attestation (D1) | Shared-secret header required only from trusted proxy addresses; health endpoint exempt | `Attestation_Required_*` (missing/wrong secret/non-trusted IP/valid-session-without-header all rejected; rejection audited) |
| 4 | Header hygiene (D3) | Forwarded headers trusted only from configured proxies; identity headers from non-trusted sources stripped and audited | `ForwardedFor_FromNonTrustedSource_IsIgnored`, `IdentityHeader_FromNonTrustedSource_IsStripped_AndAudited` |
| 5 | Session security (§3.5) | Opaque ≥128-bit ids, PBKDF2-hashed at rest, absolute + optional idle expiry, immediate revocation, cookie flags | `Create_StoresHashOnly_RawIdNeverPersistedOrReturned`, `Create_IssuesCookieWithSecurityFlags`, `IsValid_RevokedSession_RejectedImmediately`, `Invalidate_RevokesSessionImmediately_AndClearsCookie` |
| 6 | CSRF + Origin validation (D4) | Double-submit token + Origin allowlist on all cookie-authenticated requests; rejections audited | `Get_WithSessionButNoCsrfToken_IsRejected`, `Get_WithSessionAndWrongCsrfToken_IsRejected`, `Post_WithDisallowedOrigin_IsRejectedWithoutCorsHeader`, `CsrfRejections_AreAudited` |
| 7 | Login rate limiting (D7) | Lockout after N failures, persisted across restarts, generic errors | `Login_AfterMaxFailures_Returns429`, `Login_LockoutIsPersisted_SurvivesNewHost`, `Authenticate_NoCredentialConfigured_FailsGenerically` |
| 8 | Bootstrap local-only (D6) | Activation requires actual local `RemoteIpAddress`; forwarded-header spoof rejected; window limited; completion persisted and irreversible via API | `Activate_FromNonLocal_Returns403_*`, `Activate_WithForwardedHeaderSpoof_FromNonLocal_Returns403`, `AfterCompletion_Activate_And_CreateAdmin_Return403_AndAudit`, `Completion_Persists_AcrossRestart` |
| 9 | No shell execution (D12) | `systemctl show` with fixed argv, absolute path, read-only properties, allowlisted units only, hard timeout | `Provider_OnlyQueriesAllowlistedUnits`, `Provider_RequestsOnlyReadOnlyProperties`, `IsValidUnitName_RejectsInjectionAndTraversal`, `Provider_WithoutSystemctlBinary_ReportsCapabilityUnavailable` |
| 10 | No arbitrary file/process access (D10–D12) | Fixed `/proc` + `/etc/os-release` files, defensive parsers, mount filtering, statvfs | Parser fixture tests (`ParseAggregateCpu_*`, `OsReleaseParser_*`, `LinuxDiskProvider_OnLinux_ExcludesPseudoFilesystems_*`) |
| 11 | Bounded resources (D10) | Short-TTL metrics cache, per-op timeouts, capped concurrency, 10 s polling | Burst/timeout tests in provider suite; frontend `usePolling` no-overlap tests |
| 12 | Secret redaction (D7) | Denylist redaction in logging pipeline and audit persistence | `RedactJsonString_*`, `AppendAsync_ScrubsSecretMaterial*`, `Login_NeverReturnsPasswordOrHash_InBodyOrDatabase` |
| 13 | Structured errors (D7) | Error envelope, no internals in bodies, canonical codes, traceId | `ForceError_ResponseContainsNoInternalDetails`, `InternalError_MessageDoesNotContainExceptionDetails`, `UnknownRoute_Returns404Envelope` |
| 14 | Audit trail (D8, D9) | Auth events, authorization failures, first-access-per-session, audit views; failure grouping; retention prune | `FirstDataAccess_IsAuditedOnce`, `Audit_EveryView_IsAudited`, `RepeatedIdenticalFailures_AreGroupedIntoOneRow`, `PruneOlderThanAsync_*` |
| 15 | Parameterized SQL | All store access parameterized; schema initializer idempotent | `SchemaInitializer_IsIdempotent_WhenRunTwice`, store round-trip tests |
| 16 | Frontend hygiene | CSRF attached to every request; no `dangerouslySetInnerHTML`/`eval`/`innerHTML`; no token storage outside cookies | Frontend suite (43 tests); grep-verified absence of XSS sinks |

## Findings addressed during the increment

1. **Selective audit source IP was `null`** — the four data endpoints passed a
   `null` source IP to the first-access audit row. Fixed in task 13: the real
   `RemoteIpAddress` is recorded.
2. **`usePolling` fetcher-identity loop** — the polling effect re-ran on every
   render when callers passed inline closures, causing request storms. Fixed
   with the ref pattern + explicit `refreshKey` (task 15).
3. **Dead placeholder screen** — removed rather than left as unreviewed surface.

## Logged residual risks

These are accepted for this increment and must be revisited:

1. **IAP integration is validated only at deployment.** The proxy-attestation
   and forwarded-header logic is test-verified (task 9), but the real IAP
   product's behavior (header set, TLS, MFA flow) is confirmed only during the
   first production deployment. The post-deployment checklist covers this.
2. **Single admin, single password factor in the application layer.** MFA is
   delegated to the IAP. If the IAP is ever removed or bypassed on-host, the
   application has one factor. Mitigation: loopback binding + attestation;
   future: TOTP/MFA in the application itself.
3. **Audit screen cadence writes `audit.query` rows.** Every audit view is
   itself audited (by design). The screen polls at 30 s to limit growth;
   retention pruning (90 d default) bounds the table. A future server-side
   filter parameter would reduce fetched-but-unneeded rows.
4. **Client-side action filter only.** The audit endpoint has no server-side
   filter parameter; filtering happens in the browser over the loaded page.
   Not a security issue (data is already authorized), but a scalability limit.
5. **SQLite on the same host.** Audit integrity depends on host filesystem
   security. Off-host or append-only audit shipping is a future control.
6. **No runtime exploit mitigations verified in production** (e.g. seccomp
   profile beyond the systemd sandbox). `systemd-analyze security` should be
   reviewed at first deployment; tightening is cheap and additive.
7. **Dependency supply chain.** Frontend and NuGet dependencies are pinned via
   lockfiles; no automated vulnerability scanning is configured yet (CI runs
   builds/tests only). Adding `npm audit`/Dependabot is a cheap future win.

## Explicit non-claims

- No protection is claimed against a **root-compromised host** — the panel
  runs unprivileged but shares the host's fate.
- The IAP integration is **not** claimed as tested until the deployment
  checklist has been executed on the real VPS.
- This increment implements **no mutations**; any future mutation feature
  must re-enter the security review process before implementation.
