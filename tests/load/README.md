# CSTD-277 load test (JMeter, non-GUI)

These tests send read-only GET traffic to the production QA tenant only.
- **Target 1 (Next Action):** `GET /api/Engagements/{id}/next-action`. p95 < 1000 ms and errors < 1 % at 25 users.
- **Target 2 (Proof-of-Delivery report, 30-event chain):** p95 < 5000 ms and errors < 1 % at 10 users.

> AI disclosure: this suite was generated with AI assistance for QA tooling.

## Files
| File | Purpose |
|---|---|
| `custodian-load.jmx` | The test plan. **setUp** logs in once per virtual user (Identity login + select-workspace) and keeps the token in a JMeter property. Two thread groups: **Next Action** (asserts 200 and `$.engagementId`) and **Proof-of-Delivery report** (asserts 200 and `Content-Type: application/pdf`). Think time is 1–3 s random. |
| `production.properties` | The base URLs only, passed with `-q` |
| `results.properties` | Result files: CSV with no response bodies, no request/response headers and no sampler data (they would contain bearer tokens). setUp samples are excluded from the dashboard. |
| `run-step.js` | Runs one step and records it (see "How a step runs" below) |
| `prep-production.js` | Creates the QA data in production. `--resume <tag>` continues a run that stopped part-way. `--resume <tag> --pod-key pod2` builds a fresh Proof-of-Delivery engagement in the same tenant: it was used because the first one only got 22 audit events. |
| `netwatch.js` | Connection monitor to run alongside a step: every 5 s it checks a site outside Azure, Workflow and Identity, to tell a local network problem from an Azure one |
| `data/` (git-ignored) | `accounts.csv` (the QA login, **contains the password**), `engagements.csv`, `pod.csv`, `state-<tag>.json`, `prep-<tag>.txt` |
| `results/`, `reports/`, `temp/` (git-ignored) | Raw output of each run; the copies kept as evidence are in `docs/qa/evidence/cstd-277/` |

**How a step runs (`run-step.js`):**
1. A health check on Workflow, Documents and Audit (`GET /`) and on Identity (its login route).
2. An untimed warm-up.
3. JMeter, headless.
4. The health check again (a network error is retried once on a fresh connection).
5. Statistics per sampler, then a secret scan of the `.jtl`.
6. The stop rules: errors > 5 %, p95 > 10 s, or a failed health check.

The step's start and end times are recorded in UTC and Asia/Colombo.

## Properties (`-J` or a `-q` file)
| Property | Meaning |
|---|---|
| `identityUrl`, `workflowUrl`, `auditUrl` | The service base URLs |
| `naUsers` | Next Action users (0 = off) |
| `podUsers` | Proof-of-Delivery users (0 = off) |
| `rampUp`, `duration` | Seconds |
| `thinkMin`, `thinkRand` | Think time in ms (default 1000 + random 0–2000) |
| `dataDir` | Folder with the data CSVs (default `data`) |

## Re-run
```bash
cd tests/load
node prep-production.js                  # once, after approval: one new QA tenant (or --resume <tag>)
node run-step.js 1  1  0 1  180          # step 1  (baseline)
node run-step.js 5  5  0 10 180          # step 5
node run-step.js 10 10 0 20 180          # step 10
node run-step.js 25 25 0 50 180          # step 25
node run-step.js pod-1  0 1  1  180      # Proof-of-Delivery, 1 user
node run-step.js pod-5  0 5  10 180      # Proof-of-Delivery, 5 users
node run-step.js pod-10 0 10 20 180      # Proof-of-Delivery, 10 users (the target step)
node netwatch.js 330 results/netwatch-<step>.txt   # optional, in a second terminal during a step
```

Each step is the same as the JMeter command below; set `JMETER_HOME` if JMeter isn't in `C:\Users\User\Downloads\apache-jmeter-5.6.3\apache-jmeter-5.6.3`:
```bash
jmeter -n -t custodian-load.jmx -q production.properties -q results.properties \
  -JnaUsers=25 -JpodUsers=0 -JrampUp=50 -Jduration=180 -JdataDir=data \
  -l results/results-25.jtl -e -o reports/report-25 -j results/jmeter-25.log
```

## Evidence
`docs/qa/evidence/cstd-277/` holds the evidence. Its `README.md` has the results and the folder layout:
- `test-plan/`: the `.jmx` and both `.properties` files
- `test-data/`: the redacted preparation log
- `next-action/<NN>-users/` and `proof-of-delivery/<NN>-users/`, each with:
  - the HTML dashboard
  - the raw `results.jtl`
  - `step-summary.json`
  - the console log
