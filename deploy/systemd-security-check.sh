#!/usr/bin/env bash
# systemd-analyze security review helper (increment 3, D28 / plan §3.2).
#
# Runs `systemd-analyze security` for both Hosting Admin units and prints a
# summary the operator pastes into the first-deployment record table in
# docs/deployment/systemd-security-review.md §4.
#
# Usage (on the VPS, after both units are installed and active):
#   sudo ./systemd-security-check.sh
#
# Exit status: 0 if neither unit is EXPOSED (>= 9.0), 1 otherwise, 2 on
# usage/availability errors. This is a deployment-time gate, not a CI gate.

set -uo pipefail

UNITS=(hostingadmin-api.service hostingadmin-agent.service)
# systemd's own threshold: >= 9.0 is displayed as EXPOSED.
EXPOSED_THRESHOLD=9.0

if ! command -v systemd-analyze >/dev/null 2>&1; then
    echo "error: systemd-analyze not available (are we on the target host?)" >&2
    exit 2
fi

overall_rc=0

for unit in "${UNITS[@]}"; do
    echo "==================================================================="
    echo " $unit"
    echo "==================================================================="

    if ! systemctl cat "$unit" >/dev/null 2>&1; then
        echo "MISSING: unit $unit is not installed on this host." >&2
        overall_rc=1
        continue
    fi

    # Full analysis to stdout; the exposure line is extracted for the summary.
    analysis="$(systemd-analyze security "$unit" 2>&1)" || true
    printf '%s\n' "$analysis"

    exposure_line="$(printf '%s\n' "$analysis" | grep -E 'Overall exposure level' | tail -n 1 || true)"
    exposure_value="$(printf '%s\n' "$exposure_line" | grep -oE '[0-9]+\.[0-9]+' | head -n 1 || true)"

    if [ -z "$exposure_value" ]; then
        echo "WARNING: could not parse an exposure level for $unit — record manually." >&2
        overall_rc=1
    elif awk -v v="$exposure_value" -v t="$EXPOSED_THRESHOLD" 'BEGIN { exit !(v >= t) }'; then
        echo "RESULT: $unit exposure $exposure_value — EXPOSED (>= $EXPOSED_THRESHOLD): fix or accept in the record table." >&2
        overall_rc=1
    else
        echo "RESULT: $unit exposure $exposure_value — below EXPOSED threshold ($EXPOSED_THRESHOLD)."
    fi

    echo
done

echo "Paste the exposure lines above into"
echo "docs/deployment/systemd-security-review.md §4 (first-deployment record)."
exit "$overall_rc"
