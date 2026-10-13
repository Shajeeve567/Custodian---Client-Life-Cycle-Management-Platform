// CSTD-277: connectivity monitor run alongside a load step, to tell a local network problem from an Azure one.
//   node netwatch.js <seconds> <outFile>
// Every 5 s: GET https://www.cloudflare.com/cdn-cgi/trace (outside Azure) and GET / on Workflow and Identity's login
// route with an empty body (answers 400; creates nothing). Each probe uses a fresh connection, 10 s timeout.
// Writes one JSON line per probe round. No tokens are used.
const fs = require('fs');
const path = require('path');
const [secs, outFile] = process.argv.slice(2);
const props = Object.fromEntries(fs.readFileSync(path.join(__dirname, 'production.properties'), 'utf8')
    .split(/\r?\n/).filter(l => /^\w+=/.test(l)).map(l => [l.split('=')[0], l.slice(l.indexOf('=') + 1).trim()]));
const targets = {
    internet: { url: 'https://www.cloudflare.com/cdn-cgi/trace' },
    workflow: { url: props.workflowUrl + '/' },
    identity: { url: props.identityUrl + '/api/Auth/login', method: 'POST', body: '{}' },
};
async function probe({ url, method = 'GET', body }) {
    const t0 = Date.now();
    try {
        const r = await fetch(url, { method, body, headers: { Connection: 'close', ...(body ? { 'Content-Type': 'application/json' } : {}) }, signal: AbortSignal.timeout(10000) });
        await r.arrayBuffer();
        return { status: r.status, ms: Date.now() - t0 };
    } catch (e) { return { error: (e.cause && e.cause.code) || e.name, ms: Date.now() - t0 }; }
}
(async () => {
    const end = Date.now() + Number(secs) * 1000;
    fs.writeFileSync(outFile, '');
    while (Date.now() < end) {
        const t = new Date().toISOString();
        const res = Object.fromEntries(await Promise.all(Object.entries(targets).map(async ([k, v]) => [k, await probe(v)])));
        fs.appendFileSync(outFile, JSON.stringify({ t, ...res }) + '\n');
        await new Promise(r => setTimeout(r, 5000));
    }
})();
