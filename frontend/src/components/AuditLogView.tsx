import React, { useCallback, useEffect, useMemo, useState } from 'react';
import { useAuth } from '../context/AuthContext';
import { AuditEvent, ChainVerificationResult, ClientProfile, Engagement, UserAccountResponse } from '../types';
import { AuditApi, IdentityApi, WorkflowApi, ApiError } from '../services/api';
import { AuditCategory, AuditTone, describeAuditEvent, parsePayload, referencedActionId } from './audit/describeAuditEvent';
import {
    AlertTriangle,
    CheckCircle2,
    ChevronDown,
    ChevronRight,
    FileText,
    Layers,
    ListChecks,
    Loader2,
    MessageSquare,
    RefreshCw,
    ScrollText,
    Search,
    ShieldCheck,
    Flag,
    LifeBuoy,
} from 'lucide-react';

const CATEGORIES: AuditCategory[] = ['Engagement', 'Tasks', 'Questions', 'Documents', 'Conditions', 'Interventions'];

const CATEGORY_ICONS: Record<AuditCategory, React.ReactNode> = {
    Engagement: <Flag className="w-4 h-4" />,
    Tasks: <ListChecks className="w-4 h-4" />,
    Questions: <MessageSquare className="w-4 h-4" />,
    Documents: <FileText className="w-4 h-4" />,
    Conditions: <Layers className="w-4 h-4" />,
    Interventions: <LifeBuoy className="w-4 h-4" />,
};

const TONE_CLASSES: Record<AuditTone, string> = {
    neutral: 'bg-slate-100 text-slate-600 border-slate-200',
    positive: 'bg-emerald-50 text-emerald-700 border-emerald-200',
    negative: 'bg-rose-50 text-rose-700 border-rose-200',
    warning: 'bg-amber-50 text-amber-800 border-amber-200',
    info: 'bg-indigo-50 text-indigo-700 border-indigo-200',
};

const shortId = (id?: string | null) => (id ? id.slice(0, 8) : '—');
const engagementCode = (id: string) => `ENG-${id.slice(0, 6).toUpperCase()}`;

// Older events only carry task ids; name them from each engagement's task list (bounded).
const MAX_ENGAGEMENTS_TO_RESOLVE = 25;

/**
 * CSTD-40 audit trail, readable: who did what to which engagement, with the exact change, grouped by day.
 * Owner/Staff only (the API refuses clients). Each engagement has its own hash chain, verified separately.
 */
export const AuditLogView: React.FC = () => {
    const { tenantId, token } = useAuth();

    const [events, setEvents] = useState<AuditEvent[]>([]);
    const [engagements, setEngagements] = useState<Engagement[]>([]);
    const [clients, setClients] = useState<ClientProfile[]>([]);
    const [team, setTeam] = useState<UserAccountResponse[]>([]);
    const [taskTitles, setTaskTitles] = useState<Record<string, string>>({});

    const [isLoading, setIsLoading] = useState(true);
    const [error, setError] = useState<string | null>(null);

    const [engagementFilter, setEngagementFilter] = useState('');
    const [categoryFilter, setCategoryFilter] = useState<AuditCategory | ''>('');
    const [search, setSearch] = useState('');
    const [expanded, setExpanded] = useState<Set<string>>(new Set());

    const [isVerifying, setIsVerifying] = useState(false);
    const [verification, setVerification] = useState<Record<string, ChainVerificationResult> | null>(null);

    const load = useCallback(async () => {
        if (!tenantId) return;
        setIsLoading(true);
        setError(null);
        try {
            // Names are best-effort: without them the page falls back to short ids rather than failing.
            const [log, engs, clientList, teamList] = await Promise.all([
                AuditApi.getEvents(tenantId),
                WorkflowApi.getEngagements(tenantId).catch(() => [] as Engagement[]),
                IdentityApi.getClients(token || undefined).catch(() => [] as ClientProfile[]),
                token ? IdentityApi.getUsers(token).catch(() => [] as UserAccountResponse[]) : Promise.resolve([] as UserAccountResponse[]),
            ]);
            const newestFirst = [...log].sort((a, b) => new Date(b.timestamp).getTime() - new Date(a.timestamp).getTime());
            setEvents(newestFirst);
            setEngagements(engs);
            setClients(clientList);
            setTeam(teamList);
            setVerification(null);

            const needTitles = Array.from(new Set(newestFirst.filter((e) => referencedActionId(e)).map((e) => e.engagementId)))
                .slice(0, MAX_ENGAGEMENTS_TO_RESOLVE);
            const actionLists = await Promise.all(
                needTitles.map((id) => WorkflowApi.getActions(id, tenantId, false).catch(() => []))
            );
            const titles: Record<string, string> = {};
            actionLists.flat().forEach((a) => {
                titles[a.actionId] = a.title;
            });
            setTaskTitles(titles);
        } catch (err: any) {
            setError(err instanceof ApiError ? err.message : err?.message || 'Failed to load the audit log.');
        } finally {
            setIsLoading(false);
        }
    }, [tenantId, token]);

    useEffect(() => {
        load();
    }, [load]);

    const engagementName = useCallback(
        (engagementId: string) => {
            const eng = engagements.find((e) => e.engagementId === engagementId);
            const client = eng ? clients.find((c) => c.id === eng.clientId) : undefined;
            return client?.name ?? (eng ? `Client ${shortId(eng.clientId)}` : 'Unknown engagement');
        },
        [engagements, clients]
    );

    const actorName = useCallback(
        (actor: string) => {
            if (!actor) return 'Unknown';
            const user = team.find((u) => u.id === actor || u.email === actor);
            if (user) return user.email;
            const client = clients.find((c) => c.id === actor);
            if (client) return `${client.name} (client)`;
            if (/^[0-9a-f-]{36}$/i.test(actor)) return `User ${shortId(actor)}`;
            return actor === 'documents-service' ? 'Documents service' : actor;
        },
        [team, clients]
    );

    const described = useMemo(
        () =>
            events.map((evt) => ({
                evt,
                info: describeAuditEvent(evt, { taskTitle: (id) => (id ? taskTitles[id] : undefined) }),
            })),
        [events, taskTitles]
    );

    const filtered = useMemo(() => {
        const q = search.trim().toLowerCase();
        return described.filter(({ evt, info }) => {
            if (engagementFilter && evt.engagementId !== engagementFilter) return false;
            if (categoryFilter && info.category !== categoryFilter) return false;
            if (!q) return true;
            const haystack = [info.label, ...info.details, actorName(evt.actor), engagementName(evt.engagementId), engagementCode(evt.engagementId)]
                .join(' ')
                .toLowerCase();
            return haystack.includes(q);
        });
    }, [described, engagementFilter, categoryFilter, search, actorName, engagementName]);

    const byDay = useMemo(() => {
        const groups: { day: string; items: typeof filtered }[] = [];
        filtered.forEach((item) => {
            const day = new Date(item.evt.timestamp).toLocaleDateString(undefined, { weekday: 'short', year: 'numeric', month: 'short', day: 'numeric' });
            const last = groups[groups.length - 1];
            if (last && last.day === day) last.items.push(item);
            else groups.push({ day, items: [item] });
        });
        return groups;
    }, [filtered]);

    const engagementIdsInLog = useMemo(() => Array.from(new Set(events.map((e) => e.engagementId))), [events]);

    // Each engagement has its own hash chain: verify the selected one, or all in the log.
    const handleVerify = async () => {
        if (!tenantId) return;
        const ids = engagementFilter ? [engagementFilter] : engagementIdsInLog;
        setIsVerifying(true);
        try {
            const results = await Promise.all(ids.map((id) => AuditApi.verifyChain(id, tenantId)));
            const map: Record<string, ChainVerificationResult> = { ...(verification ?? {}) };
            ids.forEach((id, i) => {
                map[id] = results[i];
            });
            setVerification(map);
        } catch (err: any) {
            alert('Verification failed: ' + (err?.message || 'Server error'));
        } finally {
            setIsVerifying(false);
        }
    };

    const verificationList = verification ? Object.values(verification) : [];
    const brokenChains = verificationList.filter((v) => !v.isVerified);

    const toggle = (id: string) =>
        setExpanded((prev) => {
            const next = new Set(prev);
            if (next.has(id)) next.delete(id);
            else next.add(id);
            return next;
        });

    return (
        <div className="space-y-6">
            {/* Header */}
            <div className="flex flex-col sm:flex-row sm:items-center justify-between gap-4 pb-6 border-b border-slate-200/80">
                <div>
                    <div className="telemetry-pill mb-2">
                        <span className="telemetry-dot" />
                        <span className="telemetry-text">TAMPER-EVIDENT AUDIT TRAIL</span>
                    </div>
                    <h1 className="text-3xl font-bold text-slate-900 tracking-tight">Audit Log</h1>
                    <p className="text-sm text-slate-500 mt-1">
                        Every change to engagements, tasks, questions, documents and conditions, with who made it. Each engagement's history is hash-chained.
                    </p>
                </div>
                <div className="flex items-center gap-2">
                    <button
                        type="button"
                        onClick={load}
                        disabled={isLoading}
                        className="px-4 py-2.5 rounded-xl border border-slate-200/80 bg-white/80 hover:bg-slate-50 text-slate-700 text-sm font-semibold flex items-center gap-2 shadow-xs transition disabled:opacity-50"
                    >
                        <RefreshCw className={`w-4 h-4 text-slate-500 ${isLoading ? 'animate-spin' : ''}`} />
                        <span>Refresh</span>
                    </button>
                    <button
                        type="button"
                        onClick={handleVerify}
                        disabled={isVerifying || events.length === 0}
                        className="px-4 py-2.5 rounded-xl bg-gradient-to-r from-[#635bff] to-[#712ae2] hover:opacity-95 text-white text-sm font-semibold flex items-center gap-2 shadow-sm transition disabled:opacity-50"
                    >
                        {isVerifying ? <Loader2 className="w-4 h-4 animate-spin" /> : <ShieldCheck className="w-4 h-4" />}
                        <span>{engagementFilter ? 'Verify this engagement' : 'Verify all chains'}</span>
                    </button>
                </div>
            </div>

            {/* KPI cards */}
            <div className="grid grid-cols-1 sm:grid-cols-3 gap-4">
                <div className="bg-white/85 backdrop-blur-md p-5 rounded-2xl border border-slate-200/80 shadow-xs space-y-2">
                    <div className="flex items-center justify-between text-slate-500">
                        <span className="text-[11px] font-bold uppercase tracking-wider">Events</span>
                        <div className="p-2 rounded-xl bg-indigo-50 text-indigo-600"><ScrollText className="w-4 h-4" /></div>
                    </div>
                    <div className="text-2xl font-bold text-slate-900">{events.length}</div>
                    <p className="text-xs text-slate-500">{filtered.length === events.length ? 'All shown' : `${filtered.length} match the filters`}</p>
                </div>
                <div className="bg-white/85 backdrop-blur-md p-5 rounded-2xl border border-slate-200/80 shadow-xs space-y-2">
                    <div className="flex items-center justify-between text-slate-500">
                        <span className="text-[11px] font-bold uppercase tracking-wider">Engagements</span>
                        <div className="p-2 rounded-xl bg-slate-50 text-slate-600"><Flag className="w-4 h-4" /></div>
                    </div>
                    <div className="text-2xl font-bold text-slate-900">{engagementIdsInLog.length}</div>
                    <p className="text-xs text-slate-500">with recorded activity</p>
                </div>
                <div className="bg-white/85 backdrop-blur-md p-5 rounded-2xl border border-slate-200/80 shadow-xs space-y-2">
                    <div className="flex items-center justify-between text-slate-500">
                        <span className="text-[11px] font-bold uppercase tracking-wider">Hash chains</span>
                        <div className={`p-2 rounded-xl ${brokenChains.length > 0 ? 'bg-rose-50 text-rose-600' : 'bg-emerald-50 text-emerald-600'}`}>
                            {brokenChains.length > 0 ? <AlertTriangle className="w-4 h-4" /> : <CheckCircle2 className="w-4 h-4" />}
                        </div>
                    </div>
                    <div className={`text-2xl font-bold ${!verification ? 'text-slate-400' : brokenChains.length > 0 ? 'text-rose-700' : 'text-emerald-700'}`}>
                        {!verification ? 'Not checked' : brokenChains.length > 0 ? `${brokenChains.length} broken` : 'Intact'}
                    </div>
                    <p className="text-xs text-slate-500">
                        {verification ? `${verificationList.length} chain${verificationList.length === 1 ? '' : 's'} verified` : 'Run a verification to check'}
                    </p>
                </div>
            </div>

            {brokenChains.length > 0 && (
                <div className="p-4 rounded-2xl bg-rose-50 border border-rose-200 space-y-1.5">
                    <div className="text-sm font-bold text-rose-800 flex items-center gap-2">
                        <AlertTriangle className="w-4 h-4" /> Tampering detected
                    </div>
                    {brokenChains.map((b) => (
                        <p key={b.engagementId ?? b.brokenAtEventId} className="text-xs text-rose-800">
                            {engagementName(b.engagementId ?? '')} ({engagementCode(b.engagementId ?? '')}): {b.reason} — at event {shortId(b.brokenAtEventId)}
                        </p>
                    ))}
                </div>
            )}

            {/* Filters */}
            <div className="flex flex-wrap items-center gap-3">
                <select
                    value={engagementFilter}
                    onChange={(e) => setEngagementFilter(e.target.value)}
                    aria-label="Filter by engagement"
                    className="px-3 py-1.5 rounded-lg border border-slate-200 bg-white text-sm text-slate-700"
                >
                    <option value="">All engagements</option>
                    {engagementIdsInLog.map((id) => (
                        <option key={id} value={id}>
                            {engagementName(id)} · {engagementCode(id)}
                            {verification?.[id] ? (verification[id].isVerified ? ' ✓' : ' ✗') : ''}
                        </option>
                    ))}
                </select>
                <div className="flex flex-wrap items-center gap-1.5">
                    <button
                        type="button"
                        onClick={() => setCategoryFilter('')}
                        className={`px-2.5 py-1 rounded-full text-xs font-semibold border transition ${categoryFilter === '' ? 'bg-indigo-600 text-white border-indigo-600' : 'bg-white text-slate-600 border-slate-200 hover:bg-slate-50'}`}
                    >
                        All
                    </button>
                    {CATEGORIES.map((c) => (
                        <button
                            key={c}
                            type="button"
                            onClick={() => setCategoryFilter(c)}
                            className={`px-2.5 py-1 rounded-full text-xs font-semibold border transition flex items-center gap-1 ${categoryFilter === c ? 'bg-indigo-600 text-white border-indigo-600' : 'bg-white text-slate-600 border-slate-200 hover:bg-slate-50'}`}
                        >
                            {CATEGORY_ICONS[c]}
                            {c}
                        </button>
                    ))}
                </div>
                <div className="relative flex-1 min-w-[180px]">
                    <Search className="w-4 h-4 text-slate-400 absolute left-2.5 top-1/2 -translate-y-1/2" />
                    <input
                        type="text"
                        value={search}
                        onChange={(e) => setSearch(e.target.value)}
                        placeholder="Search tasks, documents, people…"
                        className="w-full pl-8 pr-3 py-1.5 rounded-lg border border-slate-200 bg-white text-sm text-slate-700"
                    />
                </div>
            </div>

            {/* Content */}
            {isLoading && events.length === 0 ? (
                <div className="py-16 text-center space-y-3">
                    <Loader2 className="w-8 h-8 animate-spin text-indigo-600 mx-auto" />
                    <p className="text-sm text-slate-500">Loading the audit log...</p>
                </div>
            ) : error ? (
                <div className="bg-white/85 backdrop-blur-md p-8 rounded-2xl border border-rose-200 text-center space-y-3">
                    <div className="w-12 h-12 rounded-full bg-rose-50 text-rose-600 flex items-center justify-center mx-auto">
                        <AlertTriangle className="w-6 h-6" />
                    </div>
                    <div>
                        <h3 className="text-base font-bold text-slate-900">Unable to load the audit log</h3>
                        <p className="text-sm text-slate-500 mt-1 max-w-md mx-auto">{error}</p>
                    </div>
                    <button
                        type="button"
                        onClick={load}
                        className="px-4 py-2 rounded-xl bg-indigo-600 hover:bg-indigo-700 text-white text-xs font-semibold transition inline-flex items-center gap-1.5"
                    >
                        <RefreshCw className="w-3.5 h-3.5" />
                        <span>Retry</span>
                    </button>
                </div>
            ) : filtered.length === 0 ? (
                <div className="bg-white/85 backdrop-blur-md p-12 rounded-2xl border border-dashed border-slate-300 text-center space-y-2">
                    <ScrollText className="w-8 h-8 text-slate-400 mx-auto" />
                    <h3 className="text-base font-bold text-slate-900">{events.length === 0 ? 'No activity recorded yet' : 'No events match these filters'}</h3>
                    <p className="text-sm text-slate-500">
                        {events.length === 0 ? 'Changes to engagements, tasks and documents will appear here.' : 'Try another engagement, category or search.'}
                    </p>
                </div>
            ) : (
                <div className="space-y-5">
                    {byDay.map((group) => (
                        <div key={group.day} className="space-y-2">
                            <div className="text-[11px] font-bold uppercase tracking-wider text-slate-500">{group.day}</div>
                            <div className="bg-white/85 backdrop-blur-md rounded-2xl border border-slate-200/80 shadow-xs divide-y divide-slate-100 overflow-hidden">
                                {group.items.map(({ evt, info }) => {
                                    const isOpen = expanded.has(evt.eventId);
                                    return (
                                        <div key={evt.eventId} className="px-4 py-3 hover:bg-slate-50/60 transition">
                                            <button type="button" onClick={() => toggle(evt.eventId)} className="w-full text-left flex items-start gap-3">
                                                <div className={`mt-0.5 p-2 rounded-xl border ${TONE_CLASSES[info.tone]}`}>{CATEGORY_ICONS[info.category]}</div>
                                                <div className="flex-1 min-w-0">
                                                    <div className="flex flex-wrap items-center gap-2">
                                                        <span className="text-sm font-bold text-slate-900">{info.label}</span>
                                                        <span className="text-[10px] font-semibold px-2 py-0.5 rounded-full bg-slate-100 text-slate-600 border border-slate-200">
                                                            {engagementName(evt.engagementId)} · {engagementCode(evt.engagementId)}
                                                        </span>
                                                    </div>
                                                    {info.details.length > 0 && (
                                                        <ul className="mt-1 space-y-0.5">
                                                            {info.details.map((d, i) => (
                                                                <li key={i} className="text-xs text-slate-600 break-words">{d}</li>
                                                            ))}
                                                        </ul>
                                                    )}
                                                    <div className="text-[11px] text-slate-400 mt-1">
                                                        by {actorName(evt.actor)} · {new Date(evt.timestamp).toLocaleTimeString()}
                                                    </div>
                                                </div>
                                                {isOpen ? <ChevronDown className="w-4 h-4 text-slate-400 mt-1" /> : <ChevronRight className="w-4 h-4 text-slate-400 mt-1" />}
                                            </button>
                                            {isOpen && (
                                                <div className="mt-3 ml-11 p-3 rounded-xl bg-slate-50 border border-slate-200 text-[11px] space-y-1.5">
                                                    <div className="text-slate-500">
                                                        Event <span className="font-mono text-slate-700">{evt.type}</span> · #{evt.sequenceNumber} ·{' '}
                                                        <span className="font-mono">{evt.eventId}</span>
                                                    </div>
                                                    <div className="font-mono text-slate-500 break-all">hash {evt.hash}</div>
                                                    {evt.previousHash && <div className="font-mono text-slate-400 break-all">prev {evt.previousHash}</div>}
                                                    <pre className="font-mono text-slate-700 whitespace-pre-wrap break-all bg-white p-2 rounded-lg border border-slate-200 max-h-60 overflow-auto">
                                                        {JSON.stringify(parsePayload(evt), null, 2)}
                                                    </pre>
                                                </div>
                                            )}
                                        </div>
                                    );
                                })}
                            </div>
                        </div>
                    ))}
                </div>
            )}
        </div>
    );
};
