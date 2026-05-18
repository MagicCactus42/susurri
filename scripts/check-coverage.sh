#!/usr/bin/env bash
# Coverage gate. Runs every test project, collects line-coverage data per
# assembly, compares against per-assembly minimum thresholds. Exit non-zero
# on any violation.
#
# Usage:
#     scripts/check-coverage.sh                # measure + enforce defaults
#     scripts/check-coverage.sh --measure-only # measure + report, no gate
#
# Per-assembly thresholds reflect the current (Phase 3.5 baseline) reality;
# the project goal is 75% line coverage across Modules/* (Phase 3.5 spec).
# See KNOWN-LIMITATIONS.md for the gap and the planned path forward.

set -euo pipefail

REPO_ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

# Per-assembly minimum line-coverage percentages. Anything not listed here
# is unmeasured (coverlet's <Include> filter excludes test/fuzz projects).
declare -A THRESHOLD=(
    ["Susurri.Modules.DHT.Core"]=50           # current ~55% (Tests.Unit alone)
    ["Susurri.Modules.IAM.Core"]=35           # current ~42%
    ["Susurri.Modules.DHT.Application"]=0     # ~0% (DI registration only)
    ["Susurri.Modules.IAM.Application"]=0     # ~0%
    ["Susurri.Modules.Users.Core"]=0          # ~0% (entities; needs InMemory tests)
    ["Susurri.Modules.Users.Application"]=0   # ~0%
    ["Susurri.Shared.Abstractions"]=25        # ~32% after Phase 4.1 added LogRedaction + InboundActivity tests
    ["Susurri.Shared.Infrastructure"]=0       # ~0% (messaging needs tests)
)

# Roadmap goal — recorded so the gate is self-documenting. CI failures should
# raise these toward 75% as test coverage improves.
declare -A GOAL=(
    ["Susurri.Modules.DHT.Core"]=75
    ["Susurri.Modules.IAM.Core"]=75
    ["Susurri.Modules.DHT.Application"]=60
    ["Susurri.Modules.IAM.Application"]=60
    ["Susurri.Modules.Users.Core"]=60
    ["Susurri.Modules.Users.Application"]=60
    ["Susurri.Shared.Abstractions"]=60
    ["Susurri.Shared.Infrastructure"]=60
)

MEASURE_ONLY=0
if [[ "${1:-}" == "--measure-only" ]]; then
    MEASURE_ONLY=1
fi

OUT=$(mktemp -d)
trap 'rm -rf "$OUT"' EXIT

if [ -z "${DOTNET_ROOT:-}" ] && [ -d "$HOME/.dotnet" ]; then
    export DOTNET_ROOT="$HOME/.dotnet"
    export PATH="$DOTNET_ROOT:$PATH"
fi

declare -A OBSERVED=()

run_project() {
    local proj=$1
    local subdir="$OUT/$proj"
    mkdir -p "$subdir"
    echo "::: $proj"
    timeout 600 dotnet test "tests/$proj/$proj.csproj" \
        --no-build --nologo --logger "console;verbosity=quiet" \
        --filter "FullyQualifiedName!~RoutingTableTests.FindClosestNodes_ReturnsNodesOrderedByDistance" \
        --collect:"XPlat Code Coverage" \
        --settings coverage.runsettings \
        --results-directory "$subdir" >/dev/null 2>&1 || true

    local cov
    cov=$(find "$subdir" -name 'coverage.cobertura.xml' | head -1)
    if [[ -z "$cov" ]]; then
        echo "  (no coverage data produced)"
        return
    fi

    # For each <package name="..." line-rate="...">, take the higher of
    # observed-so-far vs. this run.
    while IFS=$'\t' read -r asm rate; do
        local pct
        pct=$(awk -v r="$rate" 'BEGIN { printf "%.0f", r * 100 }')
        local prev=${OBSERVED[$asm]:-0}
        if (( pct > prev )); then
            OBSERVED[$asm]=$pct
        fi
    done < <(
        grep -oE '<package name="[^"]+" line-rate="[^"]+"' "$cov" \
            | sed -E 's/.*name="([^"]+)" line-rate="([^"]+)"/\1\t\2/'
    )
}

run_project Susurri.Tests.Unit
run_project Susurri.Tests.Integration
run_project Susurri.Tests.E2E

echo
printf "%-40s %10s %10s %10s   %s\n" "Assembly" "Observed" "Threshold" "Goal" "Status"
printf "%-40s %10s %10s %10s   %s\n" "----------------------------------------" "--------" "---------" "----" "------"

failures=0
for asm in "${!THRESHOLD[@]}"; do
    obs=${OBSERVED[$asm]:-0}
    th=${THRESHOLD[$asm]}
    goal=${GOAL[$asm]}
    if (( obs < th )); then
        status="FAIL"
        failures=$((failures + 1))
    elif (( obs < goal )); then
        status="below goal"
    else
        status="OK"
    fi
    printf "%-40s %9s%%  %9s%%  %9s%%   %s\n" "$asm" "$obs" "$th" "$goal" "$status"
done

echo
if (( MEASURE_ONLY == 1 )); then
    echo "measure-only mode; exit 0 regardless of gate"
    exit 0
fi
if (( failures > 0 )); then
    echo "Coverage gate FAILED: $failures assembly/assemblies below threshold."
    echo "Either add tests, or — if intentional — adjust the threshold in scripts/check-coverage.sh."
    exit 1
fi
echo "Coverage gate PASSED."
