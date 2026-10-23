# Load test change log

Every change to the load tests, their pass rules or their targets that was made because of what a run showed,
and why. Each entry gives its type:

- **Test fix**: the test measured something other than the system (a bug or blind spot in the test itself).
- **Rule change**: how a result is judged changed. Marked *after results* when it was made after seeing a real run.
- **Decision**: a choice about targets, environment or triggers.

Evidence folders are under `tests/jmeter/results/<run>/` (git-ignored; kept with the submission evidence).
When a verdict was recomputed, the original `sla-gate.txt` is kept and the new one is `sla-gate-regated.txt`.

## 2026-10-17

### Fail-closed target guard (test fix, `a935fd0`)
- **Change**: every HTTP request in the plan now runs only if the guard has passed; if the guard refuses, or
  never runs, the test stops before sending anything.
- **Why**: the first offline check of the guard (with a missing accounts file) crashed setUp before the guard
  ran, and the smoke group still sent about 15 unauthenticated requests (health checks and 401s) to staging and
  production. No data was read or changed, but the guard could be bypassed.
- **Evidence**: re-tested with unresolvable `.invalid` hosts and dead local ports: 0 requests sent.

### Circuit breaker uses Stop Now (test fix, `35baef7`)
- **Change**: the stress and spike circuit breaker stops the test immediately instead of gracefully.
- **Why**: a graceful stop does not end JMeter's Open Model arrival scheduler, so JMeter never exited.

## 2026-10-18 (offline, on a local mock)

### Spike phase clock starts at the end of setUp (test fix, `ea8fef9`)
- **Why**: timing phases from the first random arrival shifted every boundary by up to a few seconds, so burst
  samples were tagged as baseline.

### Open-model schedules end with a drain pause (test fix, `96a4628`)
- **Change**: stress and spike schedules end with `pause(drain)` (default 15 s).
- **Why**: when a schedule ends, JMeter shuts its thread pool and cuts off journeys still in flight
  (`Socket closed`), which counted as recovery errors on a healthy mock.

### Target: QA environment instead of staging (decision, `57dc257`)
- **Change**: tests run against the four B1 App Services with their connection strings pointed at QA databases,
  allowed by exact URL in both guards.
- **Why**: the staging App Services were on the Free tier (60 CPU-minutes per day) and differ from production;
  the QA target has production's region, plan and configuration without real data. The QA login exists only in
  the QA databases, so the setUp login fails if the apps are pointed back at real data.

## 2026-10-18 (real runs on the QA target)

### Warm-up before measuring (test fix, `20a6ec7`)
- **Why**: the first smoke run straight after the apps restarted failed on the stall queue at 4,458 ms, then
  228 ms and 404 ms on the next loops: one-off cold-start costs (JIT and query compilation, opening connections).
- **Change**: `run.sh` sends every request once in an untimed warm-up loop (`results/<run>/warmup/`), never
  gated. `--no-warmup` measures a cold start on purpose.
- **Evidence**: `20261018T165649Z-smoke` (FAIL) → `20261018T170737Z-smoke` (PASS, p95 482 ms).

### Duration Assertion first (test fix, `20a6ec7`)
- **Why**: the `.jtl` keeps the first assertion message, and a passing JSON Assertion leaves an empty one, so
  "The operation lasted too long" was hidden on every request with a JSON check.
- **Change**: the Duration Assertion now comes right after the status-code check.

### Spike stopped before recovery is a FAIL (rule change, after results, `5ecfe93`)
- **Why**: when the circuit breaker stopped the 8 journeys/s spike during the burst, the gate reported a usage
  error ("no samples in the recovery phase") instead of a verdict.
- **Change**: a run with no recovery phase is a FAIL ("did not survive the spike"). Samples JMeter cut off when
  it stopped (`Socket closed`) are left out of the statistics and counted separately, because they measure the
  test stopping, not the server.
- **Evidence**: `20261018T180045Z-spike`: verdict unchanged in substance (did not survive), now reported as FAIL.

### Stress breaking point must be sustained (rule change, after results, `f7eeee9`)
- **Why**: the gate flagged any 10 s window with a combined p95 over 800 ms, so a normal window that happened to
  include next action marked the breaking point "from 0 s at 2.6 req/s".
- **Change**: the breaking point is the first window from which every later window is over the stress limits
  (p95 2,000 ms for all requests together, or 5 % errors); the gate also reports peak sustainable throughput.
  Stress stays informational and never fails the build.
- **Evidence**: `20261018T180847Z-stress`: peak 12.4 req/s; breaking point from 120 s at about 9.4 req/s.

### Spike recovery judged by time to recover (rule change, after results, `8e2d692`)
- **Why**: the first rule compared the p95 of the whole 60 s recovery phase with the baseline, which mostly
  measured the backlog draining in its first 20 s (2.28 × baseline) and could not pass even when the system was
  back to normal.
- **Change**: recovered = the first 10 s window from which every later window has p95 ≤ 1.5 × baseline and
  ≤ 1 % errors, within 30 s of the burst ending. The whole-phase ratio is still printed for reference. The burst
  rule (at most 5 % slow requests) did not change.
- **Evidence**: `20261018T181932Z-spike`: recovered 20 s after the burst; still FAIL on the burst (15.2 %).

### Performance targets unchanged (decision)
- **Decision**: the p95 targets in `sla.json` and the per-request ceilings were not loosened. The load run
  (p95) and the sized spike (burst) stay FAIL.
- **Why**: both failures have an evidenced cause in the system, not the test: database waits that hit all four
  services at once (server CPU under a third of a core) and next action's chain of about seven sequential queries
  plus an HTTP call to Documents. Loosening the targets would hide that. The fixes are recommended in the results
  report (database tier, parallel queries in next action, rate limiting) and the same profiles can be re-run to
  show the effect.

### Legacy September plan removed (`b7399fc`)
- `custodian_performance_test.jmx` no longer matched the current authentication and had no assertions;
  `azure.properties.example` contained a default password.

## 2026-10-23

### Manual trigger only (decision)
- **Decision**: `load-test.yml` stays manual (Run workflow button or `gh workflow run`); no push, pull request or
  scheduled trigger.
- **Why**: load tests measure the deployed environment, so a pull-request run would test the code already
  deployed; they would overlap the E2E tests and deployments that run on every push; and they put real load on
  the shared QA apps and databases. Unit, mutation and E2E tests cover push and pull request.
