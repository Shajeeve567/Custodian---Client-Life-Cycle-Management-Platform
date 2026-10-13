// CSTD-277: runs one load step headless and records everything needed for the evidence.
//   node run-step.js <step> <naUsers> <podUsers> [rampUpSeconds] [durationSeconds]
// 1. health check (GET / on Workflow, Documents, Audit; Identity login route answers) - stop if any fails
// 2. untimed warm-up (a few GETs per target, NOT in the results)
// 3. jmeter -n -t custodian-load.jmx -q production.properties -q results.properties -J... -l results/results-<step>.jtl
//    -e -o reports/report-<step>/
// 4. health check again; statistics per sampler (avg, p90/p95/p99, max, throughput, error %); secret scan of the .jtl
// 5. stop rules: error rate > 5 %, p95 > 10 s or a failed health check -> exit code 3 ("do not continue")
// Writes results/step-<step>.json. Start/end are recorded in UTC and Asia/Colombo.
const fs = require('fs');
const path = require('path');
const { spawnSync } = require('child_process');

const [step, naUsers, podUsers, rampArg, durArg] = process.argv.slice(2);
if (!step || naUsers === undefined || podUsers === undefined) { console.error('usage: node run-step.js <step> <naUsers> <podUsers> [ramp] [duration]'); process.exit(2); }
const users = Math.max(Number(naUsers), Number(podUsers));
const ramp = Number(rampArg || Math.max(1, users * 2));
const duration = Number(durArg || 180);
const JMETER = process.env.JMETER_HOME ? path.join(process.env.JMETER_HOME, 'bin', 'jmeter.bat')
    : 'C:\\Users\\User\\Downloads\\apache-jmeter-5.6.3\\apache-jmeter-5.6.3\\bin\\jmeter.bat';
const dir = __dirname;
const props = Object.fromEntries(fs.readFileSync(path.join(dir, 'production.properties'), 'utf8')
    .split(/\r?\n/).filter(l => /^\w+=/.test(l)).map(l => [l.split('=')[0], l.slice(l.indexOf('=') + 1).trim()]));
fs.mkdirSync(path.join(dir, 'results'), { recursive: true });
fs.mkdirSync(path.join(dir, 'reports'), { recursive: true });
const colombo = d => new Date(d).toLocaleString('sv-SE', { timeZone: 'Asia/Colombo' }).replace(' ', 'T') + '+05:30';
const strip = t => String(t || '').replace(/^Bearer\s+/i, '');

// A network-level error (no HTTP status) is retried once after 5 s on a fresh connection: after steps 1 and 5 the
// first post-step check of Workflow failed with "fetch failed" while JMeter had 0 errors and Workflow answered 200
// seconds later (a pooled connection closed by Azure). Both attempts are recorded; only a failed retry counts.
async function health() {
    const out = {};
    const once = async (k, fresh) => {
        const t0 = Date.now();
        try {
            const r = await fetch(props[k] + '/', { headers: fresh ? { Connection: 'close' } : {}, signal: AbortSignal.timeout(60000) });
            const b = await r.text();
            return { ok: r.status === 200 && /Healthy/.test(b), status: r.status, ms: Date.now() - t0 };
        } catch (e) { return { ok: false, error: e.message + (e.cause && e.cause.code ? ` (${e.cause.code})` : ''), ms: Date.now() - t0 }; }
    };
    for (const k of ['workflowUrl', 'documentsUrl', 'auditUrl']) {
        out[k] = await once(k, false);
        if (!out[k].ok && !out[k].status) {
            await new Promise(r => setTimeout(r, 5000));
            out[k] = { ...(await once(k, true)), firstAttempt: out[k] };
        }
    }
    try {   // Identity has no health route: its login route must answer (400 for an empty body)
        const t0 = Date.now();
        const r = await fetch(props.identityUrl + '/api/Auth/login', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}', signal: AbortSignal.timeout(60000) });
        out.identityUrl = { ok: r.status === 400, status: r.status, ms: Date.now() - t0 };
    } catch (e) { out.identityUrl = { ok: false, error: e.message }; }
    return out;
}
async function warmUp() {
    const [email, password, tenant] = fs.readFileSync(path.join(dir, 'data', 'accounts.csv'), 'utf8').trim().split(/\r?\n/)[1].split(',');
    const l = await (await fetch(props.identityUrl + '/api/Auth/login', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ email, password }) })).json();
    const t = strip((await (await fetch(props.identityUrl + '/api/Auth/select-workspace/' + tenant, { method: 'POST', headers: { Authorization: 'Bearer ' + strip(l.token) } })).json()).token);
    const ids = fs.readFileSync(path.join(dir, 'data', 'engagements.csv'), 'utf8').trim().split(/\r?\n/).slice(1).map(x => x.split(',')[0]);
    for (const id of ids) for (let i = 0; i < 2; i++) await fetch(props.workflowUrl + '/api/Engagements/' + id + '/next-action', { headers: { Authorization: 'Bearer ' + t } });
    if (Number(podUsers) > 0) {
        const pod = fs.readFileSync(path.join(dir, 'data', 'pod.csv'), 'utf8').trim().split(/\r?\n/)[1].split(',')[0];
        for (let i = 0; i < 2; i++) await fetch(props.auditUrl + '/api/reports/proof-of-delivery?engagementId=' + pod + '&format=pdf', { headers: { Authorization: 'Bearer ' + t } });
    }
}
function parseCsvLine(l) {
    const out = []; let cur = '', q = false;
    for (let i = 0; i < l.length; i++) {
        const c = l[i];
        if (q) { if (c === '"' && l[i + 1] === '"') { cur += '"'; i++; } else if (c === '"') q = false; else cur += c; }
        else if (c === '"') q = true; else if (c === ',') { out.push(cur); cur = ''; } else cur += c;
    }
    out.push(cur); return out;
}
function stats(jtl) {
    // Records can span lines (quoted failure messages): split on newlines outside quotes only.
    const text = fs.readFileSync(jtl, 'utf8').trim();
    const records = []; let cur = '', q = false;
    for (const c of text) { if (c === '"') q = !q; if (c === '\n' && !q) { records.push(cur.replace(/\r$/, '')); cur = ''; } else cur += c; }
    if (cur) records.push(cur);
    const h = parseCsvLine(records[0]);
    const ix = n => h.indexOf(n);
    const rows = records.slice(1).map(parseCsvLine);
    const by = {};
    for (const r of rows) {
        const label = r[ix('label')];
        (by[label] = by[label] || []).push({ ts: Number(r[ix('timeStamp')]), el: Number(r[ix('elapsed')]), ok: r[ix('success')] === 'true', code: r[ix('responseCode')], msg: r[ix('failureMessage')] || '' });
    }
    const pct = (s, p) => s[Math.min(s.length - 1, Math.ceil(p / 100 * s.length) - 1)];
    const out = {};
    for (const [label, xs] of Object.entries(by)) {
        const el = xs.map(x => x.el).sort((a, b) => a - b);
        const first = Math.min(...xs.map(x => x.ts)), last = Math.max(...xs.map(x => x.ts + x.el));
        const errs = xs.filter(x => !x.ok);
        const codes = {}; for (const x of xs) codes[x.code] = (codes[x.code] || 0) + 1;
        const msgs = {}; for (const x of errs) { const m = String(x.msg || x.code || '').replace(/\s+/g, ' ').slice(0, 120); msgs[m] = (msgs[m] || 0) + 1; }
        out[label] = {
            samples: xs.length, avg: Math.round(el.reduce((a, b) => a + b, 0) / el.length), min: el[0], p90: pct(el, 90), p95: pct(el, 95), p99: pct(el, 99), max: el[el.length - 1],
            throughput: +(xs.length / ((last - first) / 1000)).toFixed(2), errorPct: +(100 * errs.length / xs.length).toFixed(2), responseCodes: codes, errors: msgs
        };
    }
    return out;
}

(async () => {
    const jtl = path.join(dir, 'results', `results-${step}.jtl`);
    const report = path.join(dir, 'reports', `report-${step}`);
    for (const p of [jtl, report]) if (fs.existsSync(p)) fs.rmSync(p, { recursive: true, force: true });
    const summary = { step, naUsers: Number(naUsers), podUsers: Number(podUsers), rampUp: ramp, duration, thinkMs: '1000-3000' };
    summary.healthBefore = await health();
    console.log('health before:', JSON.stringify(summary.healthBefore));
    if (Object.values(summary.healthBefore).some(h => !h.ok)) { summary.stopped = 'health check failed before the step'; fs.writeFileSync(path.join(dir, 'results', `step-${step}.json`), JSON.stringify(summary, null, 2)); console.log('STOP:', summary.stopped); process.exit(3); }
    await warmUp();
    const args = ['-n', '-t', path.join(dir, 'custodian-load.jmx'), '-q', path.join(dir, 'production.properties'), '-q', path.join(dir, 'results.properties'),
        `-JnaUsers=${naUsers}`, `-JpodUsers=${podUsers}`, `-JrampUp=${ramp}`, `-Jduration=${duration}`, `-JdataDir=${path.join(dir, 'data').replace(/\\/g, '/')}`,
        '-l', jtl, '-e', '-o', report, '-j', path.join(dir, 'results', `jmeter-${step}.log`)];
    summary.command = `jmeter ${args.map(a => a.replace(dir + path.sep, '').replace(dir.replace(/\\/g, '/') + '/', '')).join(' ')}`;
    summary.startUtc = new Date().toISOString();
    console.log(`step ${step}: ${naUsers} Next Action users, ${podUsers} PoD users, ramp ${ramp}s, ${duration}s; start ${summary.startUtc}`);
    const r = spawnSync(JMETER, args, { stdio: 'inherit', shell: true, cwd: dir });
    summary.endUtc = new Date().toISOString();
    summary.startColombo = colombo(summary.startUtc); summary.endColombo = colombo(summary.endUtc);
    summary.jmeterExit = r.status;
    summary.healthAfter = await health();
    summary.stats = fs.existsSync(jtl) ? stats(jtl) : {};
    const raw = fs.existsSync(jtl) ? fs.readFileSync(jtl, 'utf8') : '';
    summary.secretScan = /eyJ[A-Za-z0-9_-]{10,}\.|Bearer\s+\S{20,}|QaOwner-|QaPass-/.test(raw) ? 'FOUND' : 'clean';
    const target = summary.stats['GET /api/Engagements/{id}/next-action'];
    const broke = [];
    for (const [label, s] of Object.entries(summary.stats)) {
        if (label.startsWith('setUp')) continue;
        if (s.errorPct > 5) broke.push(`${label}: error rate ${s.errorPct}% > 5%`);
        if (s.p95 > 10000) broke.push(`${label}: p95 ${s.p95} ms > 10 s`);
    }
    if (Object.values(summary.healthAfter).some(h => !h.ok)) broke.push('health check failed after the step');
    summary.stopRules = broke.length ? broke : 'none triggered';
    fs.writeFileSync(path.join(dir, 'results', `step-${step}.json`), JSON.stringify(summary, null, 2) + '\n');
    console.log(JSON.stringify({ window: [summary.startUtc, summary.endUtc, summary.startColombo, summary.endColombo], stats: summary.stats, healthAfter: summary.healthAfter, secretScan: summary.secretScan, stopRules: summary.stopRules }, null, 2));
    process.exit(broke.length ? 3 : 0);
})();
