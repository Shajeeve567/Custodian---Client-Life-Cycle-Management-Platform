import React, { useState, useEffect, useCallback } from 'react';
import { useAuth } from '../context/AuthContext';
import { DashboardLayout } from '../components/DashboardLayout';
import { WorkflowApi, IdentityApi, ApiError } from '../services/api';
import { StallQueueItem, ClientProfile, UserAccountResponse, EngagementStage } from '../types';
import { RecordInterventionModal } from '../components/RecordInterventionModal';
import {
    AlertTriangle,
    Clock,
    RefreshCw,
    Loader2,
    User,
    CheckCircle2,
} from 'lucide-react';
import MissedMeetingsPanel from '../components/MissedMeetingsPanel';

function formatOverdue(hours: number): string {
    if (hours < 1) return '<1h';
    if (hours < 24) return `${hours}h`;
    const days = Math.floor(hours / 24);
    const rem = hours % 24;
    return rem === 0 ? `${days}d` : `${days}d ${rem}h`;
}

function urgencyClasses(hours: number): string {
    if (hours >= 72) return 'bg-rose-50 border-rose-200 text-rose-700';
    if (hours >= 24) return 'bg-amber-50 border-amber-200 text-amber-800';
    return 'bg-slate-50 border-slate-200 text-slate-700';
}

const STAGES: EngagementStage[] = ['Onboarding', 'DocumentCollection', 'Verification', 'Execution', 'Closure'];

// The backend caps a page at 100; the queue is expected to stay well below that.
const PAGE_SIZE = 100;

function initials(name: string): string {
    return name
        .split(' ')
        .map((n) => n[0])
        .filter(Boolean)
        .join('')
        .slice(0, 2)
        .toUpperCase() || 'CL';
}

export const StallQueuePage: React.FC = () => {
    const { tenantId, token } = useAuth();

    const [items, setItems] = useState<StallQueueItem[]>([]);
    const [totalCount, setTotalCount] = useState(0);
    const [mineOnly, setMineOnly] = useState(false);
    const [stage, setStage] = useState<EngagementStage | ''>('');
    const [clients, setClients] = useState<ClientProfile[]>([]);
    const [team, setTeam] = useState<UserAccountResponse[]>([]);

    const [isLoading, setIsLoading] = useState(true);
    const [error, setError] = useState<string | null>(null);
    const [lastRefreshed, setLastRefreshed] = useState<Date | null>(null);

    // Intervention & recovery state
    const [interveningOn, setInterveningOn] = useState<StallQueueItem | null>(null);
    const [successMessage, setSuccessMessage] = useState<string | null>(null);

    const load = useCallback(async () => {
        if (!tenantId) return;
        setIsLoading(true);
        setError(null);

        try {
            // Enrichment fetches are best-effort: if Identity is unreachable,
            // fall back to shortened IDs for display rather than failing the page.
            const [queue, clientList, teamList] = await Promise.all([
                WorkflowApi.getStallQueue(tenantId, { mine: mineOnly, stage, pageSize: PAGE_SIZE }),
                IdentityApi.getClients(token || undefined).catch(() => [] as ClientProfile[]),
                token
                    ? IdentityApi.getUsers(token).catch(() => [] as UserAccountResponse[])
                    : Promise.resolve([] as UserAccountResponse[]),
            ]);

            setItems(queue.items);
            setTotalCount(queue.totalCount);
            setClients(clientList);
            setTeam(teamList);
            setLastRefreshed(new Date());
        } catch (err: any) {
            if (err instanceof ApiError) setError(err.message);
            else setError(err?.message || 'Failed to load the stall queue.');
        } finally {
            setIsLoading(false);
        }
    }, [tenantId, token, mineOnly, stage]);

    useEffect(() => {
        load();
    }, [load]);

    const resolveClientName = (clientId: string): string => {
        const c = clients.find((x) => x.id === clientId);
        return c?.name || `Client ${clientId.slice(0, 8)}`;
    };

    const resolveStaffName = (staffId: string): string => {
        const m = team.find((x) => x.id === staffId);
        return m?.email ? m.email.split('@')[0] : `Staff ${staffId.slice(0, 8)}`;
    };

    const hasItems = items.length > 0;

    // Diagnostic: confirm the modal render path is reachable on this render pass.
    console.log('[StallQueue] render. interveningOn=', interveningOn, 'tenantId=', tenantId);

    return (
        <DashboardLayout>
            <div className="space-y-6">
                {/* Header */}
                <div className="flex flex-col sm:flex-row sm:items-center justify-between gap-4 pb-6 border-b border-slate-200/80">
                    <div>
                        <div className="telemetry-pill mb-2">
                            <span className="telemetry-dot" />
                            <span className="telemetry-text">OPERATIONAL RECOVERY QUEUE</span>
                        </div>
                        <h1 className="text-3xl font-bold text-slate-900 tracking-tight">
                            Stall Queue
                        </h1>
                        <p className="text-sm text-slate-500 mt-1">
                            Engagements with overdue client-facing actions, ordered by urgency. Intervene quickly.
                        </p>
                    </div>

                    <button
                        type="button"
                        onClick={load}
                        disabled={isLoading}
                        className="px-4 py-2.5 rounded-xl border border-slate-200/80 bg-white/80 hover:bg-slate-50 text-slate-700 text-sm font-semibold flex items-center gap-2 shadow-xs transition disabled:opacity-50"
                    >
                        <RefreshCw className={`w-4 h-4 text-slate-500 ${isLoading ? 'animate-spin' : ''}`} />
                        <span>Refresh</span>
                    </button>
                </div>

                {/* Success banner */}
                {successMessage && (
                    <div className="p-3 rounded-xl bg-emerald-50 border border-emerald-200 text-sm text-emerald-800 flex items-center gap-2">
                        <CheckCircle2 className="w-4 h-4 text-emerald-600 shrink-0" />
                        <span>{successMessage}</span>
                    </div>
                )}

                {/* Summary KPI cards */}
                <div className="grid grid-cols-1 sm:grid-cols-3 gap-4">
                    <div className="bg-white/85 backdrop-blur-md p-5 rounded-2xl border border-slate-200/80 shadow-xs space-y-2">
                        <div className="flex items-center justify-between text-slate-500">
                            <span className="text-[11px] font-bold uppercase tracking-wider">Stalled Engagements</span>
                            <div className="p-2 rounded-xl bg-rose-50 text-rose-600">
                                <AlertTriangle className="w-4 h-4" />
                            </div>
                        </div>
                        <div className="text-2xl font-bold text-slate-900">{totalCount}</div>
                        <p className="text-xs text-slate-500">Awaiting staff intervention</p>
                    </div>

                    <div className="bg-white/85 backdrop-blur-md p-5 rounded-2xl border border-slate-200/80 shadow-xs space-y-2">
                        <div className="flex items-center justify-between text-slate-500">
                            <span className="text-[11px] font-bold uppercase tracking-wider">Most Urgent</span>
                            <div className="p-2 rounded-xl bg-amber-50 text-amber-600">
                                <Clock className="w-4 h-4" />
                            </div>
                        </div>
                        <div className="text-2xl font-bold text-amber-700">
                            {hasItems ? formatOverdue(items[0].hoursOverdue) : '—'}
                        </div>
                        <p className="text-xs text-slate-500 truncate">
                            {hasItems ? items[0].blockerActionTitle : 'No stalls'}
                        </p>
                    </div>

                    <div className="bg-white/85 backdrop-blur-md p-5 rounded-2xl border border-slate-200/80 shadow-xs space-y-2">
                        <div className="flex items-center justify-between text-slate-500">
                            <span className="text-[11px] font-bold uppercase tracking-wider">Last Refreshed</span>
                            <div className="p-2 rounded-xl bg-emerald-50 text-emerald-600">
                                <CheckCircle2 className="w-4 h-4" />
                            </div>
                        </div>
                        <div className="text-lg font-bold text-slate-900">
                            {lastRefreshed ? lastRefreshed.toLocaleTimeString() : '—'}
                        </div>
                        <p className="text-xs text-slate-500">Live data — manual refresh</p>
                    </div>
                </div>

                {/* Filters */}
                <div className="flex flex-wrap items-center gap-3">
                    <label className="inline-flex items-center gap-2 text-sm text-slate-700 cursor-pointer select-none">
                        <input
                            type="checkbox"
                            checked={mineOnly}
                            onChange={(e) => setMineOnly(e.target.checked)}
                            className="rounded border-slate-300 text-indigo-600 focus:ring-indigo-500"
                        />
                        Mine only
                    </label>
                    <select
                        value={stage}
                        onChange={(e) => setStage(e.target.value as EngagementStage | '')}
                        aria-label="Filter by stage"
                        className="px-3 py-1.5 rounded-lg border border-slate-200 bg-white text-sm text-slate-700"
                    >
                        <option value="">All stages</option>
                        {STAGES.map((s) => (
                            <option key={s} value={s}>{s.replace(/([a-z])([A-Z])/g, '$1 $2')}</option>
                        ))}
                    </select>
                    {totalCount > items.length && (
                        <span className="text-xs text-slate-500">
                            Showing the {items.length} most urgent of {totalCount}
                        </span>
                    )}
                </div>

                {tenantId && <MissedMeetingsPanel tenantId={tenantId} />}

                {/* Content */}
                {isLoading && !hasItems ? (
                    <div className="py-16 text-center space-y-3">
                        <Loader2 className="w-8 h-8 animate-spin text-indigo-600 mx-auto" />
                        <p className="text-sm text-slate-500">Loading stall queue...</p>
                    </div>
                ) : error ? (
                    <div className="bg-white/85 backdrop-blur-md p-8 rounded-2xl border border-rose-200 text-center space-y-3">
                        <div className="w-12 h-12 rounded-full bg-rose-50 text-rose-600 flex items-center justify-center mx-auto">
                            <AlertTriangle className="w-6 h-6" />
                        </div>
                        <div>
                            <h3 className="text-base font-bold text-slate-900">Unable to load stall queue</h3>
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
                ) : !hasItems ? (
                    <div className="bg-white/85 backdrop-blur-md p-12 rounded-2xl border border-dashed border-emerald-300 text-center space-y-4">
                        <div className="w-12 h-12 rounded-full bg-emerald-50 text-emerald-600 flex items-center justify-center mx-auto">
                            <CheckCircle2 className="w-6 h-6" />
                        </div>
                        <div>
                            <h3 className="text-base font-bold text-slate-900">All engagements on track</h3>
                            <p className="text-sm text-slate-500 max-w-sm mx-auto mt-1">
                                {mineOnly || stage
                                    ? 'No stalled engagements match these filters.'
                                    : 'No client-facing actions are overdue. This queue populates automatically when an engagement stalls.'}
                            </p>
                        </div>
                    </div>
                ) : (
                    <div className="bg-white/85 backdrop-blur-md rounded-2xl border border-slate-200/80 shadow-xs overflow-hidden">
                        <div className="overflow-x-auto">
                            <table className="w-full text-left text-sm">
                                <thead className="bg-slate-50/80 border-b border-slate-200/80">
                                    <tr>
                                        <th className="px-4 py-3 text-[11px] font-bold uppercase tracking-wider text-slate-500">Engagement</th>
                                        <th className="px-4 py-3 text-[11px] font-bold uppercase tracking-wider text-slate-500">Blocker</th>
                                        <th className="px-4 py-3 text-[11px] font-bold uppercase tracking-wider text-slate-500">Next Action</th>
                                        <th className="px-4 py-3 text-[11px] font-bold uppercase tracking-wider text-slate-500">Overdue</th>
                                        <th className="px-4 py-3 text-[11px] font-bold uppercase tracking-wider text-slate-500">Responsible Staff</th>
                                        <th className="px-4 py-3 text-[11px] font-bold uppercase tracking-wider text-slate-500">Actions</th>
                                    </tr>
                                </thead>
                                <tbody className="divide-y divide-slate-100">
                                    {items.map((item) => {
                                        const clientName = resolveClientName(item.clientId);
                                        return (
                                            <tr key={item.engagementId} className="hover:bg-slate-50/60 transition">
                                                <td className="px-4 py-4 align-top">
                                                    <div className="flex items-start gap-3">
                                                        <div className="w-9 h-9 rounded-lg bg-gradient-to-br from-[#635bff] to-[#712ae2] text-white text-[11px] font-bold flex items-center justify-center shrink-0">
                                                            {initials(clientName)}
                                                        </div>
                                                        <div className="min-w-0">
                                                            <div className="font-semibold text-slate-900 truncate max-w-[200px]">
                                                                {clientName}
                                                            </div>
                                                            <div className="text-[11px] font-mono text-slate-400">
                                                                ENG-{item.engagementId.slice(0, 6).toUpperCase()}
                                                            </div>
                                                            <div className="text-[10px] font-semibold text-indigo-600 mt-0.5">
                                                                {item.engagementStage}
                                                            </div>
                                                        </div>
                                                    </div>
                                                </td>
                                                <td className="px-4 py-4 align-top">
                                                    <div className="font-medium text-slate-800">{item.blockerActionTitle}</div>
                                                    <div className="text-[11px] text-slate-500 mt-0.5">Stage {item.blockerStageNumber}</div>
                                                </td>
                                                <td className="px-4 py-4 align-top">
                                                    <div className="text-xs text-slate-700 max-w-[240px]">{item.nextAction}</div>
                                                    {item.nextActionResponsibleParty && (
                                                        <div className="text-[10px] font-semibold uppercase tracking-wider text-slate-400 mt-0.5">
                                                            Waiting on {item.nextActionResponsibleParty}
                                                        </div>
                                                    )}
                                                </td>
                                                <td className="px-4 py-4 align-top">
                                                    <span className={`inline-flex items-center gap-1 px-2.5 py-1 rounded-full text-xs font-bold border ${urgencyClasses(item.hoursOverdue)}`}>
                                                        <Clock className="w-3 h-3" />
                                                        {formatOverdue(item.hoursOverdue)}
                                                    </span>
                                                    <div className="text-[10px] text-slate-400 mt-1">
                                                        due {new Date(item.deadlineUtc).toLocaleDateString()}
                                                    </div>
                                                    <div className="text-[10px] text-slate-400">
                                                        stalled since {new Date(item.stalledSinceUtc).toLocaleString()}
                                                    </div>
                                                </td>
                                                <td className="px-4 py-4 align-top">
                                                    <div className="flex items-center gap-1.5 text-xs text-slate-700">
                                                        <User className="w-3.5 h-3.5 text-slate-400 shrink-0" />
                                                        <span className="truncate max-w-[160px]">{resolveStaffName(item.staffId)}</span>
                                                    </div>
                                                </td>
                                                <td className="px-4 py-4 align-top">
                                                    <button
                                                        type="button"
                                                        onClick={() => {
                                                            console.log('[StallQueue] Record Intervention clicked. item=', item);
                                                            console.log('[StallQueue] tenantId=', tenantId);
                                                            setInterveningOn(item);
                                                        }}
                                                        className="px-3 py-1.5 rounded-lg bg-amber-50 hover:bg-amber-100 border border-amber-200 text-amber-800 text-xs font-bold transition inline-flex items-center gap-1.5 whitespace-nowrap"
                                                    >
                                                        Record Intervention
                                                    </button>
                                                </td>
                                            </tr>
                                        );
                                    })}
                                </tbody>
                            </table>
                        </div>
                    </div>
                )}
            </div>

            {/* Intervention modal */}
            {interveningOn && tenantId && (
                <RecordInterventionModal
                    isOpen={!!interveningOn}
                    onClose={() => setInterveningOn(null)}
                    onRecorded={() => {
                        const label = resolveClientName(interveningOn.clientId);
                        setSuccessMessage(`Intervention recorded for ${label}.`);
                        setTimeout(() => setSuccessMessage(null), 5000);
                        load();
                    }}
                    tenantId={tenantId}
                    engagementId={interveningOn.engagementId}
                    engagementLabel={`${resolveClientName(interveningOn.clientId)} — ENG-${interveningOn.engagementId.slice(0, 6).toUpperCase()}`}
                    blockerActionId={(interveningOn as any).blockerActionId ?? null}
                    blockerActionTitle={interveningOn.blockerActionTitle}
                />
            )}
        </DashboardLayout>
    );
};

export default StallQueuePage;