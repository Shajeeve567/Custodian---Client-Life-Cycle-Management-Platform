// CSTD-277: creates the load-test data in PRODUCTION through the real APIs (run once, after approval).
//   node prep-production.js                 create everything (one new QA tenant)
//   node prep-production.js --resume <tag>  continue a run that stopped part-way: same tenant and accounts, every
//                                           step already done (recorded in data/state-<tag>.json) is skipped
//   node prep-production.js --resume <tag> --pod-key pod2
//                                           build the PoD engagement under another key (a fresh engagement in the
//                                           same tenant): run mv5kd456's "pod" was closed with only 22 audit events
//                                           because its early events were published before Kafka reached Audit
// Creates ONE QA tenant "QA CSTD277 <tag>" with an Owner (qa.cstd277.owner.<tag>@example.test) and a Client
// (qa.cstd277.client.<tag>@example.test), then:
//   na1  open client task (Onboarding)            -> Next Action target
//   na2  pending approval condition (Onboarding)  -> Next Action target
//   na3  documents verified, at Closure           -> Next Action target
//   pod  full onboarding to Closed, >= 30 audit events (Workflow/Documents -> Kafka -> Audit) -> PoD report target
// Writes (git-ignored, data/): accounts.csv (the JMeter login CSV: contains the QA password), engagements.csv and
// pod.csv (ids only), state-<tag>.json (ids + completed steps, for resume/cleanup), prep-<tag>.txt (call log: no
// tokens or passwords). The URLs come from production.properties. Nothing here runs during the load test.
const fs = require('fs');
const path = require('path');

const DAY = 864e5;
const props = Object.fromEntries(fs.readFileSync(path.join(__dirname, 'production.properties'), 'utf8')
    .split(/\r?\n/).filter(l => /^\w+=/.test(l)).map(l => [l.split('=')[0], l.slice(l.indexOf('=') + 1).trim()]));
const I = props.identityUrl, W = props.workflowUrl, D = props.documentsUrl, A = props.auditUrl;
const out = path.join(__dirname, 'data');
fs.mkdirSync(out, { recursive: true });
const resume = process.argv[2] === '--resume' ? process.argv[3] : null;
const tag = resume || Date.now().toString(36);
const podKeyAt = process.argv.indexOf('--pod-key');
const POD = podKeyAt > 0 ? process.argv[podKeyAt + 1] : 'pod';
const stateFile = path.join(out, `state-${tag}.json`);
const lines = [];
const log = s => { console.log(s); lines.push(s); };
const strip = t => String(t || '').replace(/^Bearer\s+/i, '').trim();
const claims = t => JSON.parse(Buffer.from(t.split('.')[1], 'base64url').toString('utf8'));
const sleep = ms => new Promise(r => setTimeout(r, ms));
const pdf = fs.readFileSync(path.join(__dirname, '..', 'api', 'cstd43', 'fixtures', 'sample.pdf'));

async function call(label, method, url, token, body, form) {
    const headers = token ? { Authorization: 'Bearer ' + token } : {};
    let payload;
    if (form) payload = form;
    else if (body !== undefined) { headers['Content-Type'] = 'application/json'; payload = JSON.stringify(body); }
    const t0 = Date.now();
    const r = await fetch(url, { method, headers, body: payload, signal: AbortSignal.timeout(180000) });
    const text = await r.text();
    let json = null; try { json = JSON.parse(text); } catch (e) { }
    log(`  ${label}: ${method} ${url.replace(/^https?:\/\/[^/]+/, '')} -> ${r.status} (${Date.now() - t0} ms)${r.status >= 400 ? ' ' + text.slice(0, 160).replace(/\s+/g, ' ') : ''}`);
    return { code: r.status, json, text };
}
// A 503 from a cold service-to-service check is retried (5 s apart, 3 times); anything else >= 300 stops the run.
async function must(label, ...a) {
    let r = await call(label, ...a);
    for (let i = 1; i <= 3 && r.code === 503; i++) { await sleep(5000); r = await call(`${label} (retry ${i} after 503)`, ...a); }
    if (r.code >= 300) throw new Error(`${label} -> ${r.code} ${r.text.slice(0, 200)}`);
    return r;
}
async function waitAudit(label, id, token, done, budgetMs = 300000) {
    const t0 = Date.now(); let list = [];
    while (Date.now() - t0 < budgetMs) {
        const r = await fetch(A + '/api/audit-events/engagement/' + id, { headers: { Authorization: 'Bearer ' + token } });
        if (r.ok) list = await r.json();
        if (done(list)) { log(`  ${label}: ${list.length} audit events present after ${Date.now() - t0} ms`); return list; }
        await sleep(2000);
    }
    throw new Error(`${label}: audit chain incomplete after ${budgetMs} ms (${list.length} events)`);
}

(async () => {
    const state = resume ? JSON.parse(fs.readFileSync(stateFile, 'utf8'))
        : { tag, createdAt: new Date().toISOString(), tenantName: `QA CSTD277 ${tag}`, engagements: {}, done: [] };
    state.done = state.done || [];
    const save = () => fs.writeFileSync(stateFile, JSON.stringify(state, null, 2) + '\n');
    // Runs fn once per run tag: a step recorded as done is skipped on --resume. fn may store results in `state`.
    const once = async (name, fn) => {
        if (state.done.includes(name)) { log(`  (done earlier) ${name}`); return; }
        await fn();
        state.done.push(name);
        save();
    };
    try {
        log(`CSTD-277 production data, run tag ${tag}${resume ? ' (resume)' : ''}`);
        const ownerEmail = `qa.cstd277.owner.${tag}@example.test`, ownerPw = `QaOwner-${tag}!`;
        const clientEmail = `qa.cstd277.client.${tag}@example.test`, clientPw = `QaPass-${tag}!`;
        const login = async (who, email, password) => {
            const l = await must(`login ${who}`, 'POST', I + '/api/Auth/login', null, { email, password });
            return strip((await must(`select workspace (${who})`, 'POST', I + '/api/Auth/select-workspace/' + state.tenantId, strip(l.json.token))).json.token);
        };

        await once('tenant', async () => {
            await must('register owner', 'POST', I + '/api/Auth/register', null, { email: ownerEmail, password: ownerPw });
            const l = await must('login owner', 'POST', I + '/api/Auth/login', null, { email: ownerEmail, password: ownerPw });
            state.tenantId = (await must('create tenant', 'POST', I + '/api/Tenant', strip(l.json.token), { name: state.tenantName })).json.id;
        });
        const owner = await login('owner', ownerEmail, ownerPw);
        state.ownerId = claims(owner).sub;
        await once('client', () => must('create client', 'POST', I + '/api/Client', owner, { name: 'QA CSTD277 Client', email: clientEmail, phone: null, password: clientPw }));
        const client = await login('client', clientEmail, clientPw);
        state.clientId = claims(client).client_id || claims(client).sub;
        state.accounts = [{ role: 'Owner', email: ownerEmail }, { role: 'Client', email: clientEmail }];
        save();
        // JMeter login CSV (git-ignored): the owner account; every virtual user logs in with it once in setUp.
        fs.writeFileSync(path.join(out, 'accounts.csv'), `email,password,tenantId\n${ownerEmail},${ownerPw},${state.tenantId}\n`);

        const id = key => state.engagements[key].id;
        const create = key => once(`${key} create`, async () => {
            const r = await must(key + ' create', 'POST', W + '/api/Engagements', owner, { tenantId: state.tenantId, clientId: state.clientId, staffId: state.ownerId });
            state.engagements[key] = { id: r.json.engagementId };
            save();
            await must(key + ' start', 'PUT', W + '/api/Engagements/' + r.json.engagementId + '/status', owner, { tenantId: state.tenantId, status: 'Started' });
        });
        const stage = (key, s) => once(`${key} stage ${s}`, () => must(`${key} advance to ${s}`, 'PUT', W + '/api/Engagements/' + id(key) + '/stage', owner, { tenantId: state.tenantId, stage: s }));
        const uploadAndVerify = async key => {
            for (const [type, k, off] of [['KYC_PASSPORT', 'ExpiryDate', 730], ['PROOF_OF_ADDRESS', 'IssueDate', -5]]) {
                await once(`${key} upload ${type}`, async () => {
                    const f = new FormData();
                    f.append('File', new Blob([pdf], { type: 'application/pdf' }), `qa-cstd277-${type.toLowerCase()}.pdf`);
                    f.append('Type', type); f.append(k, new Date(Date.now() + off * DAY).toISOString()); f.append('UploaderId', state.clientId);
                    // Uploaded by the Owner on the client's behalf (UploaderId = client): in production a Client upload
                    // gets 503 because Documents cannot reach Workflow for its access check (Services:WorkflowUrl).
                    const r = await must(`${key} upload ${type} (as Owner)`, 'POST', D + '/api/engagements/' + id(key) + '/documents', owner, undefined, f);
                    (state.engagements[key].documents = state.engagements[key].documents || {})[type] = r.json.documentId;
                });
            }
            await stage(key, 'Verification');
            for (const [type, docId] of Object.entries(state.engagements[key].documents)) {
                await once(`${key} verify ${type}`, () => must(`${key} verify ${type}`, 'POST', D + '/api/engagements/' + id(key) + '/documents/' + docId + '/verify', owner, { staffNotes: 'QA verified' }));
            }
        };

        // ---- Next Action engagements ----
        await create('na1');
        await once('na1 task', () => must('na1 client task', 'POST', W + '/api/engagements/' + id('na1') + '/actions', owner, { title: `QA277 provide ID ${tag}`, type: 'CustomTask', stageNumber: 1, source: 'Manual', assignedToRole: 'Client' }));
        await create('na2');
        await once('na2 approval', () => must('na2 approval condition', 'POST', W + '/api/engagements/' + id('na2') + '/conditions', owner, { type: 'Approval', requiredBeforeStage: 2, title: `QA277 scope sign-off ${tag}` }));

        // ---- PoD engagement: realistic onboarding to Closed (>= 30 audit events) ----
        await create(POD);
        const base = () => W + '/api/engagements/' + id(POD);
        const p = state.engagements[POD];
        await once(`${POD} approval`, async () => { p.approvalId = (await must(`${POD} approval`, 'POST', base() + '/conditions', owner, { type: 'Approval', requiredBeforeStage: 4, title: `QA277 sign-off ${tag}` })).json.conditionId; });
        await once(`${POD} task`, async () => {
            const t = await must(`${POD} task`, 'POST', base() + '/actions', owner, { title: `QA277 welcome call ${tag}`, type: 'CustomTask', stageNumber: 1, source: 'Manual', assignedToRole: 'Client' });
            await must(`${POD} client completes task`, 'PUT', base() + '/actions/' + t.json.actionId + '/complete', client, { completedByActor: 'client' });
        });
        await once(`${POD} requirement`, async () => { const r = await must(`${POD} requirement`, 'POST', base() + '/requirements', owner, { type: 'TaxId', stageNumber: 5, title: `QA277 tax id ${tag}`, assignedToRole: 'Client' }); p.requirementId = r.json.requirementId || r.json.id; });
        await once(`${POD} meeting`, async () => {
            const m = await must(`${POD} meeting`, 'POST', base() + '/meetings', owner, { type: 'Normal', purpose: `QA277 kickoff ${tag}`, scheduledAtUtc: new Date(Date.now() + 3 * DAY).toISOString(), durationMinutes: 30 });
            await must(`${POD} meeting reschedule`, 'POST', base() + '/meetings/' + m.json.meetingId + '/reschedule', owner, { newScheduledAtUtc: new Date(Date.now() + 4 * DAY).toISOString(), reason: 'QA client asked' });
        });
        await once(`${POD} client approves`, () => must(`${POD} client approves`, 'POST', base() + '/conditions/' + p.approvalId + '/approve', client));
        await stage(POD, 'DocumentCollection');
        await uploadAndVerify(POD);
        await stage(POD, 'Execution');
        await stage(POD, 'Closure');
        for (let i = 1; i <= 8; i++) {
            await once(`${POD} closure task ${i}`, async () => {
                const a = await must(`${POD} closure task ${i}`, 'POST', base() + '/actions', owner, { title: `QA277 closing step ${i} ${tag}`, type: 'CustomTask', stageNumber: 5, source: 'Manual', assignedToRole: 'Client' });
                await must(`${POD} client completes closure task ${i}`, 'PUT', base() + '/actions/' + a.json.actionId + '/complete', client, { completedByActor: 'client' });
            });
        }
        await once(`${POD} requirement done`, async () => {
            await must(`${POD} client submits requirement`, 'PUT', base() + '/requirements/' + p.requirementId + '/submit', client, { value: 'QA-TAX-277', submittedByActor: 'client' });
            await must(`${POD} owner approves requirement`, 'PUT', base() + '/requirements/' + p.requirementId + '/review', owner, { status: 'Approved', reviewerActor: 'staff' });
        });
        await once(`${POD} close`, () => must(`${POD} close`, 'POST', W + '/api/Engagements/' + id(POD) + '/close', owner, { summary: 'QA277 load-test engagement closed' }));
        const list = await waitAudit(POD, id(POD), owner, l => l.length >= 30 && l.some(x => x.type === 'EngagementClosed' || (x.type === 'StatusChange' && /Closed/.test(x.payload || ''))));
        p.events = list.length;

        fs.writeFileSync(path.join(out, 'pod.csv'), `engagementId,events\n${id(POD)},${list.length}\n`);
        if (POD !== 'pod') {    // only a replacement PoD engagement: the Next Action data (engagements.csv) stays as it was
            log(`done: tenant ${state.tenantId}; ${POD} ${id(POD)} (${list.length} audit events)`);
            return;
        }

        // ---- na3: documents verified, at Closure, not closed ----
        await create('na3');
        await stage('na3', 'DocumentCollection');
        await uploadAndVerify('na3');
        await stage('na3', 'Execution');
        await stage('na3', 'Closure');

        fs.writeFileSync(path.join(out, 'engagements.csv'), 'engagementId,kind\n' + ['na1', 'na2', 'na3'].map(k => `${id(k)},${k}`).join('\n') + '\n');
        log(`done: tenant ${state.tenantId}; na1 ${id('na1')}; na2 ${id('na2')}; na3 ${id('na3')}; ${POD} ${id(POD)} (${list.length} audit events)`);
    } catch (e) {
        log('ERROR: ' + e.message);
        process.exitCode = 1;
    } finally {
        save();
        fs[resume ? 'appendFileSync' : 'writeFileSync'](path.join(out, `prep-${tag}.txt`), lines.join('\n') + '\n');
    }
})();
