#!/usr/bin/env bash
# Runs one load-test profile end to end and leaves a self-contained evidence folder:
#   pre-flight (tools, accounts, target guard) -> health check -> warm-up -> JMeter (headless) -> health check
#   -> secret scan -> SLA gate.
#
# usage: ./run.sh <smoke|load|stress|spike> [--props <file>] [--data <dir>] [--no-warmup] [-- <extra JMeter args>]
#   --props  base-URL properties (default: staging.properties next to this script)
#   --data   folder with accounts.csv (default: data/ next to this script)
#   --no-warmup  skip the untimed warm-up loop (e.g. to measure a cold start on purpose)
#   after --  passed to JMeter unchanged, e.g.  ./run.sh load -- -JloadUsers=10 -JloadHold=60
#
# Output: results/<run id>/ (results.jtl, jmeter.log, health checks, sla-gate.txt, run-info.txt)
#         reports/<run id>/ (HTML dashboard) and reports/latest -> the newest dashboard.
# Exit code: 0 pass, 1 SLA failed, 2 bad usage or setup, 3 target unhealthy or refused, 4 JMeter failed.
# Environment: JMETER_BIN (JMeter command), HEALTH_ATTEMPTS / HEALTH_DELAY (health-check tries and seconds
#   between them, default 2 / 5; CI uses more because App Services restart for a minute or two after a deploy).
# Written for bash 3.2 (macOS) and Linux (CI).
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
JMETER="${JMETER_BIN:-jmeter}"
PROPS="$HERE/staging.properties"
DATA="$HERE/data"
WARMUP=1
PROFILE="${1:-}"
EXTRA=()

die() { echo "run.sh: $2" >&2; exit "$1"; }

case "$PROFILE" in
    smoke|load|stress|spike) shift ;;
    *) die 2 "usage: ./run.sh <smoke|load|stress|spike> [--props <file>] [--data <dir>] [--no-warmup] [-- <extra JMeter args>]" ;;
esac
while [ $# -gt 0 ]; do
    case "$1" in
        --props) PROPS="${2:?--props needs a file}"; shift 2 ;;
        --data)  DATA="${2:?--data needs a folder}"; shift 2 ;;
        --no-warmup) WARMUP=0; shift ;;
        --)      shift; EXTRA=("$@"); break ;;
        *)       die 2 "unknown option: $1" ;;
    esac
done

# ---------- 1. pre-flight: tools, files, and the staging-only guard (before ANY request) ----------
command -v "$JMETER" >/dev/null || die 2 "JMeter not found (install it, or set JMETER_BIN)"
command -v node >/dev/null      || die 2 "node not found (needed for the SLA gate)"
[ -f "$PROPS" ]                 || die 2 "no properties file: $PROPS"
[ -f "$DATA/accounts.csv" ]     || die 2 "no $DATA/accounts.csv (copy data/accounts.example.csv and fill in the QA login)"

prop() { sed -n "s/^$1=//p" "$PROPS" | tail -n 1 | tr -d '\r'; }
IDENTITY_URL="$(prop identityUrl)"; WORKFLOW_URL="$(prop workflowUrl)"
DOCUMENTS_URL="$(prop documentsUrl)"; AUDIT_URL="$(prop auditUrl)"

# Same rule as the guard inside the .jmx: all four URLs are Custodian staging, exactly the QA target (the four
# B1 apps re-pointed at QA databases), or all loopback. Checked here too because the health check below would
# otherwise send requests before JMeter's guard runs.
STAGING='^https://staging-[a-z]+-service-[a-z0-9]+\.centralindia-01\.azurewebsites\.net$'
LOOPBACK='^http://(localhost|127\.0\.0\.1):[0-9]+$'
QA_IDENTITY='https://custodian-identity-prod-gr-cmbqdggxh7gzevgg.southeastasia-01.azurewebsites.net'
QA_WORKFLOW='https://custodian-workflow-prod-gr-fgcecrfea6cue2d6.southeastasia-01.azurewebsites.net'
QA_DOCUMENTS='https://custodian-documents-prod-gr-cbc5b5bydgehb3cn.southeastasia-01.azurewebsites.net'
QA_AUDIT='https://custodian-audit-prod-gr-fmfbdfcnfse9bbfz.southeastasia-01.azurewebsites.net'
all_match() { local re="$1" u; for u in "$IDENTITY_URL" "$WORKFLOW_URL" "$DOCUMENTS_URL" "$AUDIT_URL"; do
    echo "$u" | grep -Eq "$re" || return 1; done; }
is_qa() { [ "$IDENTITY_URL" = "$QA_IDENTITY" ] && [ "$WORKFLOW_URL" = "$QA_WORKFLOW" ] &&
          [ "$DOCUMENTS_URL" = "$QA_DOCUMENTS" ] && [ "$AUDIT_URL" = "$QA_AUDIT" ]; }
all_match "$STAGING" || all_match "$LOOPBACK" || is_qa \
    || die 3 "refused: the URLs in $PROPS must all be Custodian staging, exactly the QA target, or all loopback"
for arg in ${EXTRA[@]+"${EXTRA[@]}"}; do
    # URLs come only from the checked --props file: no URL overrides, extra property files or remote/system props
    case "$arg" in -J*Url=*|-q*|-p*|-S*|-D*|-G*|-R*|-r) die 2 "refused: set targets with --props, not JMeter args ($arg)" ;; esac
done

# ---------- 2. fresh, timestamped output folders (a .jtl is appended to if it exists; -o refuses a used folder) ----------
RUN_ID="$(date -u +%Y%m%dT%H%M%SZ)-$PROFILE"
OUT="$HERE/results/$RUN_ID"
REPORT="$HERE/reports/$RUN_ID"
mkdir -p "$OUT" "$HERE/reports"
[ ! -e "$REPORT" ] || die 2 "report folder already exists: $REPORT"

stamp() { echo "$(date -u +%Y-%m-%dT%H:%M:%SZ) UTC / $(TZ=Asia/Colombo date +%Y-%m-%dT%H:%M:%S) Asia/Colombo"; }
{
    echo "profile:   $PROFILE"
    echo "targets:   $PROPS"
    echo "           identity=$IDENTITY_URL"
    echo "           workflow=$WORKFLOW_URL"
    echo "           documents=$DOCUMENTS_URL"
    echo "           audit=$AUDIT_URL"
    echo "extra:     ${EXTRA[*]+"${EXTRA[*]}"}"
    echo "warm-up:   $([ "$WARMUP" = 1 ] && echo "yes (one smoke loop, not measured)" || echo no)"
    echo "jmeter:    $("$JMETER" --version 2>/dev/null | grep -Eo '[0-9]+\.[0-9]+(\.[0-9]+)?' | head -n 1)"
    echo "started:   $(stamp)"
} > "$OUT/run-info.txt"

# ---------- 3. health check: is every service up before we load it? (also wakes a cold App Service) ----------
# Workflow, Documents and Audit answer GET / with {"status":"Healthy"}. Identity has no health route, so its
# login is called with an empty body: 400 (validation error) proves the app and its pipeline are running.
check() {   # check <name> <expected status> <curl args...>
    local name="$1" want="$2" code attempt=1 attempts="${HEALTH_ATTEMPTS:-2}"; shift 2
    while [ "$attempt" -le "$attempts" ]; do
        code="$(curl -sS -o "$OUT/health-body.tmp" -w '%{http_code}' --max-time 60 "$@" 2>/dev/null || true)"
        if [ "$code" = "$want" ] && { [ "$want" != 200 ] || grep -q Healthy "$OUT/health-body.tmp"; }; then
            echo "  ok    $name ($code)"; return 0
        fi
        if [ "$attempt" -lt "$attempts" ]; then sleep "${HEALTH_DELAY:-5}"; fi
        attempt=$((attempt + 1))
    done
    echo "  FAIL  $name (got ${code:-no response}, expected $want)"; return 1
}
health() {   # health <before|after>
    local failed=0
    echo "Health check ($1 the run):"
    check identity  400 -X POST -H 'Content-Type: application/json' -d '{}' "$IDENTITY_URL/api/Auth/login" || failed=1
    check workflow  200 "$WORKFLOW_URL/"  || failed=1
    check documents 200 "$DOCUMENTS_URL/" || failed=1
    check audit     200 "$AUDIT_URL/"     || failed=1
    rm -f "$OUT/health-body.tmp"
    return $failed
}
if ! health before | tee "$OUT/health-before.txt"; then
    die 3 "a service is not healthy: not starting the load (see $OUT/health-before.txt)"
fi

# ---------- 3b. warm-up: one untimed smoke loop, kept apart from the results and never gated ----------
# A freshly (re)started App Service pays one-off costs on its first real requests: .NET JIT compilation, EF Core
# query compilation, opening the MySQL connection pool and cold database caches. Measuring those would describe the
# restart, not the system under load, so every request is sent once first. --no-warmup skips this.
if [ "$WARMUP" = 1 ]; then
    echo; echo "Warm-up: one untimed smoke loop -> $OUT/warmup (not part of the results)"
    mkdir -p "$OUT/warmup"
    set +e
    "$JMETER" -n -t "$HERE/custodian-load.jmx" \
        -q "$PROPS" -q "$HERE/report.properties" \
        -Jprofile=smoke -JsmokeLoops=1 -JdataDir="$DATA" \
        -l "$OUT/warmup/warmup.jtl" -j "$OUT/warmup/jmeter.log" > "$OUT/warmup/console.txt" 2>&1
    WARMUP_EXIT=$?
    set -e
    [ "$WARMUP_EXIT" = 0 ] || die 4 "warm-up JMeter run failed (see $OUT/warmup/jmeter.log)"
    grep -E '^summary =' "$OUT/warmup/console.txt" | tail -n 1 | sed 's/^/  /'
fi

# ---------- 4. JMeter, headless ----------
echo; echo "JMeter: profile $PROFILE -> $OUT"
set +e
"$JMETER" -n -t "$HERE/custodian-load.jmx" \
    -q "$PROPS" -q "$HERE/report.properties" \
    -Jprofile="$PROFILE" -JdataDir="$DATA" \
    ${EXTRA[@]+"${EXTRA[@]}"} \
    -l "$OUT/results.jtl" -j "$OUT/jmeter.log" -e -o "$REPORT"
JMETER_EXIT=$?
set -e
echo "finished:  $(stamp)" >> "$OUT/run-info.txt"
[ "$JMETER_EXIT" = 0 ] || die 4 "JMeter exited with $JMETER_EXIT (see $OUT/jmeter.log)"
if grep -q 'Circuit breaker OPEN' "$OUT/jmeter.log"; then
    echo "circuit breaker: $(grep -m 1 'Circuit breaker OPEN' "$OUT/jmeter.log" | sed 's/.*Circuit breaker/Circuit breaker/')" | tee -a "$OUT/run-info.txt"
fi

# ---------- 5. did the system survive? (informational: the SLA gate decides pass/fail) ----------
echo
health after | tee "$OUT/health-after.txt" || true

# ---------- 6. secret scan: evidence folders must never contain a token or the QA password ----------
PASSWORDS="$(tail -n +2 "$DATA/accounts.csv" | cut -d, -f2 | grep -v '^$' || true)"
LEAK=0
grep -rlE 'eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}' "$OUT" "$REPORT" >/dev/null 2>&1 && LEAK=1   # a JWT
while IFS= read -r pw; do   # passwords under 8 characters are skipped: they would match ordinary dashboard text
    [ "${#pw}" -ge 8 ] && grep -rlF -- "$pw" "$OUT" "$REPORT" >/dev/null 2>&1 && LEAK=1
done <<EOF
$PASSWORDS
EOF
[ "$LEAK" = 0 ] || die 2 "secret scan: a token or password was found in $OUT or $REPORT - do not share them"
echo; echo "Secret scan: clean"
ln -sfn "$RUN_ID" "$HERE/reports/latest"   # only a dashboard that passed the scan becomes "latest"

# ---------- 7. SLA gate ----------
echo
set +e
case "$PROFILE" in
    smoke|load)   node "$HERE/sla-gate.js" --profile "$PROFILE" --report "$REPORT" ;;
    stress|spike) node "$HERE/sla-gate.js" --profile "$PROFILE" --jtl "$OUT/results.jtl" ;;
esac > "$OUT/sla-gate.txt" 2>&1
GATE=$?
set -e
cat "$OUT/sla-gate.txt"
echo "sla gate:  exit $GATE" >> "$OUT/run-info.txt"

echo
echo "Evidence:  $OUT"
echo "Dashboard: $REPORT/index.html   (also reports/latest/index.html)"
exit "$GATE"
