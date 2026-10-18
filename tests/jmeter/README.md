# Load & Performance Testing (JMeter)

**Owner:** Gunitha Rathnayake (IT24610787), SE3112 Advanced Software Engineering, *Load & Performance Testing* role.

This suite load-tests the Custodian microservices (Identity, Workflow, Documents, Audit) with Apache JMeter 5.6.3.
One test plan holds four test types (**smoke, load, stress, spike**), a runner script turns a run into
evidence plus a PASS/FAIL result, and a manual GitHub Actions workflow can deploy and test from a button.

## Contents

- [What is tested](#what-is-tested)
- [Files](#files)
- [Setup](#setup)
- [Targets and safety](#targets-and-safety)
- [Running a test](#running-a-test)
- [Profiles](#profiles)
- [Results and SLA gate](#results-and-sla-gate)
- [CI: the "Run workflow" button](#ci-the-run-workflow-button)
- [Troubleshooting](#troubleshooting)

## What is tested

Every virtual user is a staff member (Owner) of one QA tenant. setUp signs each QA account in once
(`login` → `Tenant/mine` → `select-workspace`, which gives the tenant-scoped token the APIs require), so the
password hashing at login is not mixed into the read measurements.

| Request | Service | Why |
|---|---|---|
| `GET /` | Workflow, Documents, Audit | Health (smoke only) |
| `GET /api/Engagements` | Workflow | Dashboard list; picks a random engagement for the rest of the journey |
| `GET /api/Engagements/{id}`, `/next-action` | Workflow | Engagement page and the next-action rules |
| `GET /api/engagements/{id}/documents` | Documents | Documents tab |
| `GET /api/audit-events/engagement/{id}` | Audit | Audit trail tab |
| `GET /api/Notifications/unread-count` | Identity | A light authenticated read that keeps Identity under load after the one-off login (the API exists; the current frontend does not call it yet) |
| `GET /api/stall-queue` | Workflow | Aggregate query (heavier) |
| `GET /api/reports/sla-performance?format=pdf` | Workflow | PDF rendering (heaviest) |
| Unknown engagement → **404**, invalid token → **401** | Workflow | Error paths must stay correct and fast under load |

Under load, one iteration is one **staff journey**: list → open an engagement and its next action → documents
tab (50 %) → audit tab (30 %) → unread count → stall queue (20 %) → PDF report (5 %) → negative cases (5 %).
The weights are Throughput Controllers and can be changed with `-JpctDocuments`, `-JpctAudit`, `-JpctStall`,
`-JpctReport` and `-JpctNegative`.

## Files

| File | Purpose |
|---|---|
| `custodian-load.jmx` | The test plan: setUp, the four profiles, and a Test Fragment with every request defined once |
| `run.sh` | Runs one profile end to end: guard → health check → warm-up → JMeter → health check → secret scan → SLA gate |
| `sla.json` | Performance targets (p95 per request) and the pass rules of each profile |
| `sla-gate.js` | Turns a run into PASS / FAIL (exit code), used by `run.sh` and CI |
| `staging.properties` | Base URLs of the staging App Services |
| `qa.properties` | Base URLs of the QA target (see below) |
| `report.properties` | Dashboard settings (title, APDEX, filters) and the extra `spikePhase` result column |
| `data/accounts.example.csv` | Template for `data/accounts.csv` (the QA login, **git-ignored**) |
| `results/`, `reports/` | Output of each run (git-ignored) |

## Setup

1. **Java 17+ and JMeter 5.6.3.** macOS: `brew install jmeter`. Elsewhere: download the binary from
   [jmeter.apache.org](https://jmeter.apache.org/download_jmeter.cgi), unpack it and put `bin/` on your `PATH`,
   or point `JMETER_BIN` at `bin/jmeter`.
2. **Node.js 20** (for the SLA gate). Already required by the frontend.
3. **The QA login.** Copy the template and fill in a QA account that is an Owner of a QA tenant with at least one
   engagement:
   ```bash
   cp tests/jmeter/data/accounts.example.csv tests/jmeter/data/accounts.csv
   ```
   Several rows are allowed: virtual users are spread across the accounts round-robin. Never commit this file.

## Targets and safety

A target is a properties file with the four base URLs. Both `run.sh` and the guard inside the `.jmx` refuse to
start unless **all four** URLs are one of:

| Target | File | Notes |
|---|---|---|
| Staging | `staging.properties` | `staging-*-service-*.centralindia-01.azurewebsites.net`, deployed from `dev` |
| QA | `qa.properties` | The Sprint 4 B1 App Services with their connection strings re-pointed at **QA databases**, allowed by exact URL only. Same region, plan and configuration as production, so the numbers are realistic |
| Loopback | your own file | `http://localhost:<port>` / `127.0.0.1`: a local stack or a mock |

- **Fail closed.** Every HTTP request in the plan runs only if the guard passed. If the guard refuses, or setUp
  fails (missing `accounts.csv`, bad login), the test stops before any load is sent.
- **QA canary login.** A hostname cannot prove which database an app uses, so the QA account must exist **only
  in the QA databases**. If the QA apps are ever pointed back at real data, setUp's login fails and nothing runs.
- Targets come only from `--props`: `run.sh` refuses URL overrides (`-J…Url=`) and extra property files (`-q`).
- Before using the QA target: apply the same migrations as `main` to the QA databases, and switch the
  connection strings back before anyone demos the production frontend.
- Run heavy profiles (stress, spike) only when teammates are not testing the same environment.

## Running a test

From `tests/jmeter/`:

```bash
./run.sh smoke --props qa.properties
./run.sh load  --props qa.properties
./run.sh stress --props qa.properties
./run.sh spike --props qa.properties
```

Without `--props` the target is `staging.properties`. Anything after `--` is passed to JMeter, so every property
below can be tuned without editing the plan:

```bash
./run.sh load --props qa.properties -- -JloadUsers=30 -JloadHold=300
```

Before measuring, `run.sh` sends every request once in an untimed **warm-up** loop (kept in `results/<run>/warmup/`,
never gated). A freshly restarted App Service pays one-off costs on its first real requests (JIT and EF Core query
compilation, opening the MySQL connection pool, cold caches): a first smoke run straight after a restart saw the
stall queue at 4.5 s and the PDF at 9 s, then 0.2–0.4 s on the next loops. Add `--no-warmup` to measure a cold
start on purpose.

`run.sh` exit codes: **0** pass · **1** SLA failed · **2** bad usage/setup or a secret found in the output ·
**3** target refused or unhealthy · **4** JMeter could not run.

The same run without the script (no health check, secret scan or gate):

```bash
jmeter -n -t custodian-load.jmx -q qa.properties -q report.properties -Jprofile=load -l results/load.jtl -j results/load.log -e -o reports/load
```

Open the plan in the GUI to read or edit it (do not run load from the GUI):

```bash
jmeter -t custodian-load.jmx
```

## Profiles

| Profile | Model | Default shape | Question | Pass rule (`sla.json`) |
|---|---|---|---|---|
| `smoke` | Closed (Thread Group) | 1 user × 3 loops, every request | Does the script and the deployment work at all? | 0 % errors |
| `load` | Closed (Thread Group) | 20 users, 60 s ramp-up, 180 s hold | Does it meet its targets at the expected concurrency? | Every request within its p95, errors < 1 % |
| `stress` | Open (Open Model Thread Group) | 1 → 2 → 4 → 6 → 8 journeys/s, 60 s per step | Where does it break, and what fails first? | Informational: reports the breaking point |
| `spike` | Open (Open Model Thread Group) | 1/s for 60 s → 8/s for 30 s → 1/s for 60 s | Does it survive a burst and recover? | ≤ 5 % errors in the burst, ≤ 1 % after, recovery p95 ≤ 1.5 × baseline. Stopped by the circuit breaker before recovery = FAIL (did not survive) |

**Closed model:** a fixed number of users, each waiting for its response before the next request. **Open model:**
journeys arrive at a set rate whether or not the server keeps up, so a slow server shows up as a growing number
of active threads. Stress and spike end with a short `pause` (`-Jdrain`) so journeys in flight can finish.

**Circuit breaker:** stress stops itself when 100 consecutive samples have more than 20 % errors (spike: 50 %),
so a broken system is not kept under load. Requests still in flight when it stops end with `Socket closed`: the gate leaves those out, because they
measure the test stopping, not the server. The console logs one line per window, e.g.
`stress window: 100 samples, 14.0% errors, 11 active threads`.

| Property (`-J`) | Default | Meaning |
|---|---|---|
| `smokeUsers`, `smokeLoops` | 1, 3 | Smoke size |
| `loadUsers`, `loadRampUp`, `loadHold` | 20, 60, 180 | Users, seconds to start them, seconds at full load |
| `stressLevels`, `stressStep`, `stressRamp` | 1,2,4,6,8 · 60 · 10 | Journeys/s per step, seconds held, seconds to climb |
| `spikeBaseRate`, `spikePeakRate` | 1, 8 | Journeys/s normally and during the burst |
| `spikeBase`, `spikeRise`, `spikeHold`, `spikeRecover` | 60, 2, 30, 60 | Phase lengths in seconds |
| `stressAbortErrorPct`, `spikeAbortErrorPct` | 20, 50 | Circuit-breaker limit per 100 samples |
| `thinkMin`, `thinkRand` | 1000, 2000 | Load think time: min + random(0..rand) ms (`smokeThink*`, `stressThink*`, `spikeThink*` for the others) |
| `maxResponseMs`, `maxReportMs` | 3000, 10000 | Hard ceiling per request: slower counts as a failure |
| `drain` | 15 | Seconds at the end of stress/spike with no new arrivals |
| `randomSeed` | 0 | Fix it to repeat the same random arrivals |

## Results and SLA gate

Each run gets its own folders (JMeter appends to an existing `.jtl`, so folders are never reused):

```text
results/<UTC time>-<profile>/   run-info.txt (targets, settings, start/end in UTC and Asia/Colombo),
                                health-before.txt, health-after.txt, results.jtl, jmeter.log, sla-gate.txt,
                                warmup/ (the untimed warm-up loop)
reports/<UTC time>-<profile>/   HTML dashboard (index.html); reports/latest points to the newest one
```

**Two layers of performance checks:**

- **Duration Assertions** in the plan fail any single request slower than `maxResponseMs` (or `maxReportMs` for
  the PDF). Such a request counts as an error.
- **p95 targets** in `sla.json` are checked over the whole run by `sla-gate.js`: 800 ms by default, 500 ms for the
  unread count, 1.5 s for the stall queue, 3 s for the PDF. p95 is used rather than the average because one slow
  request among many fast ones barely moves the average.

**Reading the dashboard:** APDEX (satisfied ≤ 800 ms, tolerated ≤ 3 s; PDF 3 s / 10 s), Statistics (p90/p95/p99
and error % per request), Errors, and the *Over Time* graphs: Response Times, Active Threads and Transactions per
Second. In stress and spike, rising active threads at a constant arrival rate means the server is saturating.
setUp samples are left out of the dashboard. Spike samples carry a `spikePhase` column (baseline / spike /
recovery) in `results.jtl`, and the gate compares the phases.

The gate can also be run on its own:

```bash
node sla-gate.js --profile load --report reports/latest
node sla-gate.js --profile spike --jtl results/<run>/results.jtl
```

## CI: the "Run workflow" button

`.github/workflows/load-test.yml` runs **only** when someone presses **Run workflow**: never on push, pull
request or a schedule. In GitHub: **Actions → Load Test — JMeter (manual) → Run workflow**, then choose:

- **Run the JMeter load test after the deploy**: ticked = deploy, then test. Unticked = only rebuild, unit-test
  and redeploy the four services.
- **Load-test profile**: smoke, load, stress or spike.

What happens: the four existing `deploy-*.yml` workflows run in parallel (build, unit tests, deploy). Only when
all four succeed, the JMeter job installs JMeter 5.6.3 (checksum verified), writes `data/accounts.csv` from
secrets, waits for the apps to come up, and runs `./run.sh <profile> --props qa.properties`. The SLA table
appears on the run's summary page and the results and dashboard are kept for 30 days as an artifact. The job is
green only if `run.sh` exits 0. Only one run at a time is allowed.

Required repository secrets (GitHub → Settings → Secrets and variables → Actions): `QA_EMAIL` and `QA_PASSWORD`
(the QA-only login), plus the existing `AZURE_<SERVICE>_APP_NAME` / `_PUBLISH_PROFILE` used by the deploy
workflows.

GitHub's runners are far from Azure Southeast Asia, so every request from CI includes extra network time. Smoke
results from CI are reliable; for load, stress and spike figures, run `run.sh` from a machine close to the target.

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| `refused: the URLs in … must all be …` | The target is not staging, exactly the QA target, or loopback. Check the properties file |
| `no data/accounts.csv` | Create it from `accounts.example.csv` |
| `a service is not healthy` | An app is down or still starting. In CI, `HEALTH_ATTEMPTS` × `HEALTH_DELAY` sets how long to wait |
| setUp `login` fails, test stops at once | Wrong password, or the account does not exist in this target's database (which is the canary doing its job on a non-QA database) |
| `Cannot write to …` when running JMeter by hand | The `-o` folder must not exist yet and its parent must exist. `run.sh` handles this |
| Statistics look doubled or mixed | The `.jtl` already existed and JMeter appended to it. Use a new file (`run.sh` always does) |
| A request fails with `success=false`, status 200 and no message | It was slower than `maxResponseMs` / `maxReportMs` (Duration Assertion). The first requests after an app restart are slow: let the warm-up run (the default) |
| `WARNING: package sun.awt.X11…`, `StatusConsoleListener` lines | Harmless messages from JMeter's Java libraries at startup |
