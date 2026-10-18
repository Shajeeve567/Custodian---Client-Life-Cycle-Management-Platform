#!/usr/bin/env node
// SLA gate for custodian-load.jmx: turns a JMeter run into PASS / FAIL so a script or CI job can act on it.
//
//   node sla-gate.js --profile smoke|load  --report reports/<run>          (reads the dashboard's statistics.json)
//   node sla-gate.js --profile stress|spike --jtl results/<run>.jtl        (reads the raw samples)
//   options: --sla <file> (default: sla.json next to this script)
//
// Exit code: 0 = pass (stress is informational and always passes), 1 = SLA failed, 2 = bad input / nothing ran.
'use strict';
const fs = require('fs');
const path = require('path');

const SETUP_LABEL = /^(setUp:|Guard:)/;   // run once before the load; never judged (same filter as the dashboard)

function parseArgs(argv) {
    const args = {};
    for (let i = 0; i < argv.length; i += 2) {
        if (!argv[i].startsWith('--') || argv[i + 1] === undefined) usage(`unexpected argument: ${argv[i]}`);
        args[argv[i].slice(2)] = argv[i + 1];
    }
    return args;
}

function usage(message) {
    console.error(`sla-gate: ${message}\nusage: node sla-gate.js --profile <smoke|load|stress|spike> (--report <dir> | --jtl <file>) [--sla <file>]`);
    process.exit(2);
}

// Minimal RFC 4180 CSV reader: .jtl failure messages can contain commas, quotes and newlines.
function readCsv(file) {
    const text = fs.readFileSync(file, 'utf8');
    const rows = [];
    let row = [], field = '', quoted = false;
    for (let i = 0; i < text.length; i++) {
        const c = text[i];
        if (quoted) {
            if (c === '"' && text[i + 1] === '"') { field += '"'; i++; }
            else if (c === '"') quoted = false;
            else field += c;
        } else if (c === '"') quoted = true;
        else if (c === ',') { row.push(field); field = ''; }
        else if (c === '\n' || c === '\r') {
            if (c === '\r' && text[i + 1] === '\n') i++;
            row.push(field); rows.push(row); row = []; field = '';
        } else field += c;
    }
    if (field !== '' || row.length) { row.push(field); rows.push(row); }
    const [header, ...data] = rows;
    return data.filter(r => r.length === header.length).map(r => Object.fromEntries(header.map((h, i) => [h, r[i]])));
}

// "Socket closed" = JMeter closed the connection itself while the request was in flight, which is what Stop Now
// (the circuit breaker) does. Those samples measure the test stopping, not the server, so they are left out
// of the statistics and reported separately. A server-side drop shows up differently (e.g. "Connection reset").
const CUT_OFF_BY_STOP = s => /SocketException/.test(s.responseCode) && /Socket closed/.test(s.responseMessage);

function readSamples(file) {
    if (!fs.existsSync(file)) usage(`no such .jtl: ${file}`);
    const all = readCsv(file).filter(s => !SETUP_LABEL.test(s.label));
    const samples = all.filter(s => !CUT_OFF_BY_STOP(s));
    if (samples.length === 0) usage(`no load samples in ${file} (did the guard or setUp stop the test?)`);
    if (samples.length < all.length)
        console.log(`Left out ${all.length - samples.length} samples cut off by the test stopping (Socket closed): not server errors.\n`);
    return samples;
}

// Nearest-rank percentile, as used for SLAs ("95 % of requests were at or below X ms").
function percentile(values, p) {
    const sorted = [...values].sort((a, b) => a - b);
    return sorted[Math.max(0, Math.ceil((p / 100) * sorted.length) - 1)];
}

function summarise(samples) {
    const elapsed = samples.map(s => Number(s.elapsed));
    const errors = samples.filter(s => s.success !== 'true').length;
    return {
        count: samples.length,
        p95: percentile(elapsed, 95),
        errorPct: (100 * errors) / samples.length,
        maxThreads: Math.max(...samples.map(s => Number(s.allThreads))),
    };
}

const p95Limit = (sla, label) => sla.p95Ms[label] ?? sla.p95Ms.default;
const fmt = (n, digits = 0) => Number(n).toFixed(digits);

// smoke / load: per-label checks on the dashboard's aggregated statistics.
function gateStatistics(rules, sla, reportDir) {
    const file = path.join(reportDir, 'statistics.json');
    if (!fs.existsSync(file)) usage(`no statistics.json in ${reportDir} (was the run started with -e -o?)`);
    const stats = JSON.parse(fs.readFileSync(file, 'utf8'));
    const labels = Object.keys(stats).filter(k => k !== 'Total' && !SETUP_LABEL.test(k)).sort();
    if (labels.length === 0) usage(`no load samples in ${file}`);

    const failures = [];
    console.log(`${'label'.padEnd(64)} ${'samples'.padStart(7)} ${'p95 ms'.padStart(7)} ${'limit'.padStart(6)} ${'err %'.padStart(6)}  result`);
    for (const label of labels) {
        const s = stats[label];
        const limit = p95Limit(sla, label);
        const problems = [];
        if (s.errorPct > rules.maxErrorPct) problems.push(`errors ${fmt(s.errorPct, 2)}% > ${rules.maxErrorPct}%`);
        if (rules.checkP95 && s.pct2ResTime > limit) problems.push(`p95 ${fmt(s.pct2ResTime)} ms > ${limit} ms`);
        failures.push(...problems.map(p => `${label}: ${p}`));
        console.log(`${label.padEnd(64)} ${String(s.sampleCount).padStart(7)} ${fmt(s.pct2ResTime).padStart(7)} ` +
            `${(rules.checkP95 ? String(limit) : '-').padStart(6)} ${fmt(s.errorPct, 2).padStart(6)}  ${problems.length ? 'FAIL' : 'ok'}`);
    }
    const total = stats.Total;
    console.log(`\nTotal: ${total.sampleCount} samples, ${fmt(total.throughput, 1)} req/s, p95 ${fmt(total.pct2ResTime)} ms, errors ${fmt(total.errorPct, 2)}%`);
    return failures;
}

// stress: time windows -> where did it break, and what was the most it sustained? Informational only.
// A window is over the limit when its error rate or its p95 (all requests together) is above the stress limits.
// The breaking point is the first window from which EVERY later window is over the limit: a one-off slow window
// (e.g. a database hiccup) is not a breaking point, sustained degradation is. Peak sustainable throughput is the
// best req/s before that point.
function gateStress(rules, samples) {
    const t0 = Math.min(...samples.map(s => Number(s.timeStamp)));
    const size = rules.bucketSeconds * 1000;
    const buckets = new Map();
    for (const s of samples) {
        const b = Math.floor((Number(s.timeStamp) - t0) / size);
        if (!buckets.has(b)) buckets.set(b, []);
        buckets.get(b).push(s);
    }
    const windows = [...buckets].sort((x, y) => x[0] - y[0]).map(([b, list]) => {
        const s = summarise(list);
        return { from: b * rules.bucketSeconds, rate: list.length / rules.bucketSeconds, over: s.errorPct > rules.maxErrorPct || s.p95 > rules.p95Ms, ...s };
    });
    let firstSustained = windows.length;
    while (firstSustained > 0 && windows[firstSustained - 1].over) firstSustained--;
    const breaking = windows[firstSustained];
    const before = windows.slice(0, firstSustained);
    const peak = before.length ? before.reduce((a, w) => (w.rate > a.rate ? w : a)) : null;

    console.log(`${'window'.padEnd(12)} ${'req/s'.padStart(6)} ${'p95 ms'.padStart(7)} ${'err %'.padStart(6)} ${'threads'.padStart(8)}`);
    windows.forEach((w, i) => console.log(`${`${w.from}-${w.from + rules.bucketSeconds}s`.padEnd(12)} ${fmt(w.rate, 1).padStart(6)} ` +
        `${String(w.p95).padStart(7)} ${fmt(w.errorPct, 1).padStart(6)} ${String(w.maxThreads).padStart(8)}` +
        `${i === firstSustained ? '  <- breaking point (over the limit from here on)' : i > firstSustained ? '  over limit'
            : w.over ? '  (one-off: recovered)' : ''}`));

    console.log(`\nLimits: p95 ${rules.p95Ms} ms, errors ${rules.maxErrorPct}% per ${rules.bucketSeconds} s window.`);
    if (peak) console.log(`Peak sustainable throughput: ${fmt(peak.rate, 1)} req/s (${peak.from}-${peak.from + rules.bucketSeconds}s, ` +
        `${peak.maxThreads} active threads, p95 ${peak.p95} ms).`);
    console.log(breaking
        ? `Breaking point: from ${breaking.from}s, at ~${fmt(breaking.rate, 1)} req/s and ${breaking.maxThreads} active threads ` +
          `(p95 ${breaking.p95} ms, errors ${fmt(breaking.errorPct, 1)}%); every later window stayed over the limit.`
        : 'No breaking point within this run: it never stayed over the limit.');
    return [];
}

// spike: compare the phases recorded in the spikePhase column.
function gateSpike(rules, samples) {
    if (!('spikePhase' in samples[0])) usage('the .jtl has no spikePhase column (run with -q report.properties)');
    const phases = Object.fromEntries(['baseline', 'spike', 'recovery'].map(p => [p, samples.filter(s => s.spikePhase === p)]));
    if (phases.baseline.length === 0) usage('no samples in the baseline phase');
    const [base, spike, rec] = ['baseline', 'spike', 'recovery'].map(p => phases[p].length ? summarise(phases[p]) : null);

    console.log(`${'phase'.padEnd(10)} ${'samples'.padStart(7)} ${'p95 ms'.padStart(7)} ${'err %'.padStart(6)} ${'threads'.padStart(8)}`);
    for (const [name, s] of [['baseline', base], ['spike', spike], ['recovery', rec]])
        console.log(s ? `${name.padEnd(10)} ${String(s.count).padStart(7)} ${String(s.p95).padStart(7)} ${fmt(s.errorPct, 1).padStart(6)} ${String(s.maxThreads).padStart(8)}`
                      : `${name.padEnd(10)} ${'-'.padStart(7)}   (not reached)`);

    // The run never got to recovery: the circuit breaker stopped it during the burst, so it did not survive.
    if (!spike || !rec) {
        const where = spike ? `during the spike (${fmt(spike.errorPct, 1)}% errors, ${spike.maxThreads} active threads)` : 'before the spike';
        return [`did not survive the spike: the test was stopped ${where}, so the recovery phase was never reached`];
    }

    const failures = [];
    const ratio = rec.p95 / base.p95;
    if (spike.errorPct > rules.maxErrorPctSpike) failures.push(`spike: errors ${fmt(spike.errorPct, 1)}% > ${rules.maxErrorPctSpike}%`);
    if (rec.errorPct > rules.maxErrorPctRecovery) failures.push(`recovery: errors ${fmt(rec.errorPct, 1)}% > ${rules.maxErrorPctRecovery}%`);
    if (ratio > rules.maxRecoveryP95Ratio) failures.push(`recovery: p95 is ${fmt(ratio, 2)}x baseline > ${rules.maxRecoveryP95Ratio}x (did not recover)`);
    console.log(`\nRecovery p95 / baseline p95 = ${fmt(ratio, 2)}x (limit ${rules.maxRecoveryP95Ratio}x); spike p95 = ${fmt(spike.p95 / base.p95, 2)}x baseline.`);
    return failures;
}

function main() {
    const args = parseArgs(process.argv.slice(2));
    const sla = JSON.parse(fs.readFileSync(args.sla || path.join(__dirname, 'sla.json'), 'utf8'));
    const rules = sla.profiles[args.profile];
    if (!rules) usage(`unknown profile "${args.profile}" (expected one of: ${Object.keys(sla.profiles).join(', ')})`);

    console.log(`SLA gate - profile ${args.profile}\n`);
    let failures;
    if (args.profile === 'smoke' || args.profile === 'load') {
        if (!args.report) usage('--report <dashboard dir> is required for smoke and load');
        failures = gateStatistics(rules, sla, args.report);
    } else {
        if (!args.jtl) usage('--jtl <results file> is required for stress and spike');
        const samples = readSamples(args.jtl);
        failures = args.profile === 'stress' ? gateStress(rules, samples) : gateSpike(rules, samples);
    }

    if (failures.length) {
        console.log(`\nRESULT: FAIL (${failures.length})`);
        failures.forEach(f => console.log(`  - ${f}`));
        process.exit(1);
    }
    console.log(`\nRESULT: PASS${args.profile === 'stress' ? ' (informational profile)' : ''}`);
}

main();
