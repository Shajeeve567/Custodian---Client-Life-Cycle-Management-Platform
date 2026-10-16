import React, { useCallback, useEffect, useMemo, useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { useAuth } from '../context/AuthContext';
import { DashboardLayout } from '../components/DashboardLayout';
import { IdentityApi, WorkflowApi, ApiError } from '../services/api';
import { ClientAction, ClientProfile, Engagement, UserAccountResponse } from '../types';
import { ENGAGEMENT_STAGES } from '../constants/engagementStages';
import {
    engagementOptionLabel,
    formatDate,
    personLabel,
    taskSourceLabel,
    taskTypeLabel,
    verificationStatusLabel,
} from '../utils/displayLabels';
import {
    AlertTriangle,
    Ban,
    Calendar,
    CheckCircle2,
    Clock,
    ExternalLink,
    Eye,
    ListChecks,
    Loader2,
    Lock,
    RefreshCw,
    UploadCloud,
    User,
    XCircle,
} from 'lucide-react';

const STATUS_FILTERS = ['All', 'Pending', 'Uploaded', 'Completed', 'Rejected', 'Cancelled'] as const;
type StatusFilter = (typeof STATUS_FILTERS)[number];

const isFinished = (a: ClientAction) => a.status === 'Completed' || a.status === 'Cancelled' || Boolean(a.isCompleted);
const isOverdue = (a: ClientAction) =>
    !isFinished(a) && Boolean(a.deadlineUtc) && new Date(a.deadlineUtc as string).getTime() < Date.now();

const formatDateTime = (value?: string | null) => (value ? new Date(value).toLocaleString() : '—');

const StatusBadge: React.FC<{ action: ClientAction }> = ({ action }) => {
    const base = 'inline-flex items-center gap-1 px-2.5 py-0.5 rounded-full text-[11px] font-bold border';
    switch (action.status) {
        case 'Completed':
            return <span className={`${base} bg-emerald-50 text-emerald-700 border-emerald-200`}><CheckCircle2 className="w-3 h-3" />Completed</span>;
        case 'Uploaded':
            return <span className={`${base} bg-sky-50 text-sky-700 border-sky-200`}><UploadCloud className="w-3 h-3" />Uploaded</span>;
        case 'Rejected':
            return <span className={`${base} bg-rose-50 text-rose-700 border-rose-200`}><XCircle className="w-3 h-3" />Rejected</span>;
        case 'Cancelled':
            return <span className={`${base} bg-slate-100 text-slate-600 border-slate-200`}><Ban className="w-3 h-3" />No longer required</span>;
        default:
            return isOverdue(action)
                ? <span className={`${base} bg-rose-50 text-rose-700 border-rose-200`}><AlertTriangle className="w-3 h-3" />Overdue</span>
                : <span className={`${base} bg-amber-50 text-amber-700 border-amber-200`}><Clock className="w-3 h-3" />Pending</span>;
    }
};

const MetaItem: React.FC<{ label: string; children: React.ReactNode; tone?: 'danger' }> = ({ label, children, tone }) => (
    <div className="min-w-0">
        <dt className="text-[10px] font-bold uppercase tracking-wider text-slate-500">{label}</dt>
        <dd className={`text-xs mt-0.5 ${tone === 'danger' ? 'text-rose-700 font-semibold' : 'text-slate-700'}`}>{children}</dd>
    </div>
);

/**
 * CSTD-28: the full task record of one engagement — every task with its dates, who completed it,
 * and its verification outcome. Read-only: tasks are worked on in the engagement's workspace,
 * which applies the evidence and approval rules this page would otherwise bypass.
 */
export const ActionHistoryPage: React.FC = () => {
    const { tenantId, token, userId, role } = useAuth();
    const [searchParams, setSearchParams] = useSearchParams();
    const selectedEngagementId = searchParams.get('engagementId') || '';

    const [engagements, setEngagements] = useState<Engagement[]>([]);
    const [clients, setClients] = useState<ClientProfile[]>([]);
    const [team, setTeam] = useState<UserAccountResponse[]>([]);
    const [actions, setActions] = useState<ClientAction[]>([]);
    const [isLoadingEngagements, setIsLoadingEngagements] = useState(true);
    const [isLoading, setIsLoading] = useState(false);
    const [error, setError] = useState<string | null>(null);
    const [statusFilter, setStatusFilter] = useState<StatusFilter>('All');
    const [isClientView, setIsClientView] = useState(false);

    const selectEngagement = useCallback(
        (engagementId: string) => setSearchParams(engagementId ? { engagementId } : {}, { replace: true }),
        [setSearchParams]
    );

    useEffect(() => {
        if (!tenantId) return;
        setIsLoadingEngagements(true);
        WorkflowApi.getEngagements(tenantId)
            .then((list) => {
                setEngagements(list);
                if (!selectedEngagementId && list.length > 0) selectEngagement(list[0].engagementId);
            })
            .catch((err) => setError(err instanceof ApiError ? err.message : 'Failed to load engagements.'))
            .finally(() => setIsLoadingEngagements(false));
        if (token) {
            IdentityApi.getClients(token).then(setClients).catch(() => setClients([]));
            if (role === 'Owner') IdentityApi.getUsers(token).then(setTeam).catch(() => setTeam([]));
        }
        // Only on workspace change; the selection itself is handled below.
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [tenantId, token, role]);

    const loadActions = useCallback(async () => {
        if (!selectedEngagementId || !tenantId) return;
        setIsLoading(true);
        setError(null);
        try {
            const list = await WorkflowApi.getActions(selectedEngagementId, tenantId, isClientView);
            setActions(list || []);
        } catch (err: any) {
            setActions([]);
            setError(err instanceof ApiError ? err.message : 'Failed to load the action history.');
        } finally {
            setIsLoading(false);
        }
    }, [selectedEngagementId, tenantId, isClientView]);

    useEffect(() => {
        loadActions();
    }, [loadActions]);

    const clientNameFor = (eng?: Engagement) => (eng && clients.find((c) => c.id === eng.clientId)?.name) || 'Unnamed client';
    const selectedEngagement = engagements.find((e) => e.engagementId === selectedEngagementId);

    const counts = useMemo(() => {
        const byStatus: Record<string, number> = { All: actions.length };
        actions.forEach((a) => {
            const key = a.status || 'Pending';
            byStatus[key] = (byStatus[key] ?? 0) + 1;
        });
        return {
            byStatus,
            completed: byStatus.Completed ?? 0,
            open: actions.filter((a) => !isFinished(a)).length,
            overdue: actions.filter(isOverdue).length,
        };
    }, [actions]);

    // Oldest stage first, then by creation time, so the list reads like the engagement's timeline.
    const visibleActions = useMemo(
        () =>
            actions
                .filter((a) => statusFilter === 'All' || (a.status || 'Pending') === statusFilter)
                .sort((a, b) => (a.stageNumber ?? 1) - (b.stageNumber ?? 1) || a.createdAt.localeCompare(b.createdAt)),
        [actions, statusFilter]
    );

    const stageName = (stageNumber?: number) => ENGAGEMENT_STAGES[(stageNumber ?? 1) - 1]?.name ?? `Stage ${stageNumber}`;

    return (
        <DashboardLayout>
            <div className="space-y-6">
                {/* Header */}
                <div className="flex flex-col sm:flex-row sm:items-center justify-between gap-4 pb-6 border-b border-slate-200/80">
                    <div>
                        <div className="telemetry-pill mb-2">
                            <span className="telemetry-dot" />
                            <span className="telemetry-text">ENGAGEMENT TASK RECORD</span>
                        </div>
                        <h1 className="text-3xl font-bold text-slate-900 tracking-tight">Action History</h1>
                        <p className="text-sm text-slate-500 mt-1">
                            Every task of an engagement: when it was created and activated, its deadline, who completed it and how it was verified.
                        </p>
                    </div>

                    <div className="flex flex-wrap items-center gap-2">
                        {selectedEngagementId && (
                            <Link
                                to={`/workspace/${selectedEngagementId}`}
                                className="px-4 py-2.5 rounded-xl border border-indigo-200 bg-indigo-50/60 hover:bg-indigo-50 text-indigo-700 text-sm font-semibold flex items-center gap-2 shadow-xs transition"
                            >
                                <ExternalLink className="w-4 h-4" />
                                <span>Open workspace</span>
                            </Link>
                        )}
                        <button
                            type="button"
                            onClick={loadActions}
                            disabled={isLoading || !selectedEngagementId}
                            className="px-4 py-2.5 rounded-xl border border-slate-200/80 bg-white/80 hover:bg-slate-50 text-slate-700 text-sm font-semibold flex items-center gap-2 shadow-xs transition disabled:opacity-50"
                        >
                            <RefreshCw className={`w-4 h-4 text-slate-500 ${isLoading ? 'animate-spin' : ''}`} />
                            <span>Refresh</span>
                        </button>
                    </div>
                </div>

                {/* Engagement picker and view */}
                <div className="flex flex-col lg:flex-row lg:items-center justify-between gap-3 bg-white/85 backdrop-blur-md p-4 rounded-2xl border border-slate-200/80 shadow-xs">
                    <div className="flex flex-col sm:flex-row sm:items-center gap-2 min-w-0">
                        <label htmlFor="action-history-engagement" className="text-xs font-bold text-slate-500 uppercase tracking-wider shrink-0">
                            Engagement
                        </label>
                        <select
                            id="action-history-engagement"
                            value={selectedEngagementId}
                            onChange={(e) => selectEngagement(e.target.value)}
                            disabled={isLoadingEngagements || engagements.length === 0}
                            className="w-full sm:w-auto min-w-0 px-3 py-2 rounded-xl border border-slate-200 bg-white text-sm text-slate-800 focus:outline-none focus:ring-2 focus:ring-indigo-500/20 focus:border-indigo-500"
                        >
                            {engagements.length === 0 && (
                                <option value="">{isLoadingEngagements ? 'Loading engagements…' : 'No engagements yet'}</option>
                            )}
                            {engagements.map((eng) => (
                                <option key={eng.engagementId} value={eng.engagementId}>
                                    {engagementOptionLabel(eng, clientNameFor(eng))}
                                </option>
                            ))}
                        </select>
                    </div>

                    <div className="flex items-center gap-1 bg-slate-100 p-1 rounded-xl text-xs self-start lg:self-auto" role="group" aria-label="Whose view">
                        <button
                            type="button"
                            onClick={() => setIsClientView(false)}
                            aria-pressed={!isClientView}
                            className={`px-3 py-1.5 rounded-lg font-semibold transition flex items-center gap-1.5 ${!isClientView ? 'bg-white text-indigo-700 shadow-xs' : 'text-slate-600 hover:text-slate-900'}`}
                        >
                            <Lock className="w-3.5 h-3.5" />
                            Staff view
                        </button>
                        <button
                            type="button"
                            onClick={() => setIsClientView(true)}
                            aria-pressed={isClientView}
                            className={`px-3 py-1.5 rounded-lg font-semibold transition flex items-center gap-1.5 ${isClientView ? 'bg-white text-indigo-700 shadow-xs' : 'text-slate-600 hover:text-slate-900'}`}
                        >
                            <Eye className="w-3.5 h-3.5" />
                            What the client sees
                        </button>
                    </div>
                </div>

                {/* Summary cards */}
                <div className="grid grid-cols-2 lg:grid-cols-4 gap-4">
                    {[
                        { label: 'Total tasks', value: actions.length, note: selectedEngagement ? clientNameFor(selectedEngagement) : '—', icon: <ListChecks className="w-4 h-4" />, iconCls: 'bg-indigo-50 text-indigo-600', valueCls: 'text-slate-900' },
                        { label: 'Completed', value: counts.completed, note: 'Done or approved', icon: <CheckCircle2 className="w-4 h-4" />, iconCls: 'bg-emerald-50 text-emerald-600', valueCls: 'text-emerald-700' },
                        { label: 'Open', value: counts.open, note: 'Pending, uploaded or rejected', icon: <Clock className="w-4 h-4" />, iconCls: 'bg-amber-50 text-amber-600', valueCls: 'text-amber-700' },
                        { label: 'Overdue', value: counts.overdue, note: 'Open and past the deadline', icon: <AlertTriangle className="w-4 h-4" />, iconCls: 'bg-rose-50 text-rose-600', valueCls: counts.overdue > 0 ? 'text-rose-700' : 'text-slate-900' },
                    ].map((card) => (
                        <div key={card.label} className="bg-white/85 backdrop-blur-md p-5 rounded-2xl border border-slate-200/80 shadow-xs space-y-2">
                            <div className="flex items-center justify-between text-slate-500">
                                <span className="text-[11px] font-bold uppercase tracking-wider">{card.label}</span>
                                <div className={`p-2 rounded-xl ${card.iconCls}`}>{card.icon}</div>
                            </div>
                            <div className={`text-2xl font-bold ${card.valueCls}`}>{isLoading ? '—' : card.value}</div>
                            <p className="text-xs text-slate-500 truncate">{card.note}</p>
                        </div>
                    ))}
                </div>

                {/* Status filter */}
                <div className="flex flex-wrap items-center gap-1.5" role="group" aria-label="Filter by status">
                    {STATUS_FILTERS.map((status) => (
                        <button
                            key={status}
                            type="button"
                            onClick={() => setStatusFilter(status)}
                            aria-pressed={statusFilter === status}
                            className={`px-3.5 py-1.5 rounded-xl text-xs font-semibold border transition ${
                                statusFilter === status
                                    ? 'bg-indigo-50 text-indigo-700 border-indigo-200'
                                    : 'bg-white/80 text-slate-600 border-slate-200 hover:bg-slate-50'
                            }`}
                        >
                            {status === 'All' ? 'All tasks' : status === 'Cancelled' ? 'No longer required' : status}
                            <span className="ml-1.5 text-slate-400">{counts.byStatus[status] ?? 0}</span>
                        </button>
                    ))}
                </div>

                {/* Content */}
                {isLoading && actions.length === 0 ? (
                    <div className="py-16 text-center space-y-3">
                        <Loader2 className="w-8 h-8 animate-spin text-indigo-600 mx-auto" />
                        <p className="text-sm text-slate-500">Loading action history...</p>
                    </div>
                ) : error ? (
                    <div className="bg-white/85 backdrop-blur-md p-8 rounded-2xl border border-rose-200 text-center space-y-3">
                        <div className="w-12 h-12 rounded-full bg-rose-50 text-rose-600 flex items-center justify-center mx-auto">
                            <AlertTriangle className="w-6 h-6" />
                        </div>
                        <div>
                            <h3 className="text-base font-bold text-slate-900">Unable to load the action history</h3>
                            <p className="text-sm text-slate-500 mt-1 max-w-md mx-auto">{error}</p>
                        </div>
                        <button
                            type="button"
                            onClick={loadActions}
                            className="px-4 py-2 rounded-xl bg-indigo-600 hover:bg-indigo-700 text-white text-xs font-semibold transition inline-flex items-center gap-1.5"
                        >
                            <RefreshCw className="w-3.5 h-3.5" />
                            <span>Retry</span>
                        </button>
                    </div>
                ) : visibleActions.length === 0 ? (
                    <div className="bg-white/85 backdrop-blur-md p-12 rounded-2xl border border-dashed border-slate-300 text-center space-y-3">
                        <div className="w-12 h-12 rounded-full bg-indigo-50 text-indigo-600 flex items-center justify-center mx-auto">
                            <ListChecks className="w-6 h-6" />
                        </div>
                        <h3 className="text-base font-bold text-slate-900">
                            {engagements.length === 0 && !isLoadingEngagements ? 'No engagements yet' : 'No tasks to show'}
                        </h3>
                        <p className="text-sm text-slate-500 max-w-sm mx-auto">
                            {engagements.length === 0 && !isLoadingEngagements
                                ? 'Create an engagement from the Engagements page to start tracking its tasks.'
                                : statusFilter !== 'All'
                                ? `No tasks with the status "${statusFilter === 'Cancelled' ? 'No longer required' : statusFilter}".`
                                : 'Tasks appear here once they are added in the engagement workspace.'}
                        </p>
                    </div>
                ) : (
                    <ul className="space-y-3">
                        {visibleActions.map((action) => {
                            const assignedRole = action.assignedToRole ?? action.assignedRole;
                            const overdue = isOverdue(action);
                            return (
                                <li
                                    key={action.actionId}
                                    className={`bg-white/90 backdrop-blur-md p-5 rounded-2xl border shadow-xs space-y-3 ${
                                        action.status === 'Cancelled' ? 'border-slate-200 opacity-70' : overdue ? 'border-rose-200' : 'border-slate-200/90'
                                    }`}
                                >
                                    <div className="flex flex-col sm:flex-row sm:items-start justify-between gap-3">
                                        <div className="min-w-0 space-y-1.5">
                                            <div className="flex flex-wrap items-center gap-1.5">
                                                <span className="text-[10px] font-semibold px-2 py-0.5 rounded bg-slate-100 text-slate-700 border border-slate-200">
                                                    Stage {action.stageNumber ?? 1} · {stageName(action.stageNumber)}
                                                </span>
                                                <span className="text-[10px] font-bold px-2 py-0.5 rounded bg-white text-indigo-700 border border-indigo-100">
                                                    {taskTypeLabel(action.type)}
                                                </span>
                                                {!isClientView && (
                                                    <span className="text-[10px] font-semibold px-2 py-0.5 rounded bg-indigo-50/80 text-indigo-600 border border-indigo-100">
                                                        {taskSourceLabel(action.sourceType)}
                                                    </span>
                                                )}
                                                {action.isInternalOnly && (
                                                    <span className="inline-flex items-center gap-1 text-[10px] font-bold px-2 py-0.5 rounded bg-amber-50 text-amber-800 border border-amber-200">
                                                        <Lock className="w-3 h-3" /> Staff only
                                                    </span>
                                                )}
                                            </div>
                                            <h3 className={`text-sm font-bold ${action.status === 'Cancelled' ? 'text-slate-500 line-through' : 'text-slate-900'}`}>
                                                {action.title}
                                            </h3>
                                            {action.description && <p className="text-xs text-slate-600 leading-relaxed">{action.description}</p>}
                                        </div>
                                        <div className="flex flex-wrap items-center gap-1.5 shrink-0">
                                            {action.verificationStatus && action.verificationStatus.toUpperCase() !== 'PENDING' && (
                                                <span
                                                    className={`text-[11px] font-bold px-2.5 py-0.5 rounded-full border ${
                                                        action.verificationStatus.toUpperCase() === 'VERIFIED'
                                                            ? 'bg-emerald-50 text-emerald-700 border-emerald-200'
                                                            : 'bg-rose-50 text-rose-700 border-rose-200'
                                                    }`}
                                                >
                                                    {verificationStatusLabel(action.verificationStatus)}
                                                </span>
                                            )}
                                            <StatusBadge action={action} />
                                        </div>
                                    </div>

                                    {action.verificationReason && (
                                        <p
                                            className={`text-xs p-2.5 rounded-lg border ${
                                                action.verificationStatus?.toUpperCase() === 'REJECTED'
                                                    ? 'bg-rose-50 border-rose-100 text-rose-700'
                                                    : 'bg-emerald-50/60 border-emerald-100 text-emerald-800'
                                            }`}
                                        >
                                            <span className="font-semibold">
                                                {action.verificationStatus?.toUpperCase() === 'REJECTED' ? 'Rejection reason: ' : 'Verification notes: '}
                                            </span>
                                            {action.verificationReason}
                                        </p>
                                    )}

                                    <dl className="grid grid-cols-2 sm:grid-cols-3 lg:grid-cols-6 gap-3 pt-3 border-t border-slate-100">
                                        <MetaItem label="Assigned to">
                                            <span className="inline-flex items-center gap-1">
                                                <User className="w-3 h-3 text-slate-400" />
                                                {assignedRole ?? (isClientView ? 'Client' : '—')}
                                            </span>
                                        </MetaItem>
                                        <MetaItem label="Created">{formatDateTime(action.createdAt)}</MetaItem>
                                        <MetaItem label="Activated">{formatDateTime(action.activatedAt)}</MetaItem>
                                        <MetaItem label="Deadline" tone={overdue ? 'danger' : undefined}>
                                            <span className="inline-flex items-center gap-1">
                                                <Calendar className="w-3 h-3 text-slate-400" />
                                                {action.deadlineUtc ? formatDate(action.deadlineUtc) : 'None'}
                                            </span>
                                        </MetaItem>
                                        <MetaItem label="Completed by">
                                            {action.status === 'Completed' || action.isCompleted
                                                ? action.completedByActor
                                                    ? personLabel(action.completedByActor, { users: team, currentUserId: userId })
                                                    : 'System'
                                                : '—'}
                                        </MetaItem>
                                        <MetaItem label="Completed on">{formatDateTime(action.completedAt)}</MetaItem>
                                    </dl>

                                    {!isClientView && action.sourceMetadata && (
                                        <details className="text-[11px] text-slate-500">
                                            <summary className="cursor-pointer font-semibold text-slate-600 hover:text-slate-800">Technical details</summary>
                                            <pre className="mt-2 font-mono text-slate-700 whitespace-pre-wrap break-all bg-slate-50 p-2 rounded-lg border border-slate-200 max-h-48 overflow-auto">
                                                {(() => {
                                                    try {
                                                        return JSON.stringify(JSON.parse(action.sourceMetadata), null, 2);
                                                    } catch {
                                                        return action.sourceMetadata;
                                                    }
                                                })()}
                                            </pre>
                                        </details>
                                    )}
                                </li>
                            );
                        })}
                    </ul>
                )}
            </div>
        </DashboardLayout>
    );
};
