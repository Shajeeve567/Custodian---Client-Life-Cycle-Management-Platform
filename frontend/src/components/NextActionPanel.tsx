import React from 'react';
import { NextActionItem, NextActionResult } from '../types';
import {
    AlertTriangle,
    ArrowRight,
    CheckCircle2,
    ChevronRight,
    Clock,
    Info,
    Loader2,
    PauseCircle,
    RefreshCw,
    ShieldAlert,
    User,
    Users,
    WifiOff,
} from 'lucide-react';

interface NextActionPanelProps {
    result: NextActionResult | null;
    isLoading: boolean;
    error: string | null;
    isAdvancing: boolean;
    onAdvance: () => void;
    onRefresh: () => void;
}

const STATE_STYLES: Record<string, { label: string; className: string }> = {
    ClientActionRequired: { label: 'Client action required', className: 'bg-amber-50 text-amber-800 border-amber-200' },
    AwaitingStaff: { label: 'Awaiting staff', className: 'bg-indigo-50 text-indigo-800 border-indigo-200' },
    ReadyToAdvance: { label: 'Ready to advance', className: 'bg-emerald-50 text-emerald-800 border-emerald-200' },
    BlockedExternal: { label: 'Dependency unavailable', className: 'bg-rose-50 text-rose-800 border-rose-200' },
    NotStarted: { label: 'Not started', className: 'bg-slate-100 text-slate-700 border-slate-200' },
    AllComplete: { label: 'All complete', className: 'bg-emerald-50 text-emerald-800 border-emerald-200' },
    Closed: { label: 'Closed', className: 'bg-slate-100 text-slate-700 border-slate-200' },
};

// .NET TimeSpan JSON ("d.hh:mm:ss" or "hh:mm:ss") → "2d 3h" / "4h 10m" / "25m".
const formatOverdueBy = (value?: string | null): string | null => {
    if (!value) return null;
    const match = value.match(/^(?:(\d+)\.)?(\d+):(\d+)/);
    if (!match) return value;
    const days = Number(match[1] ?? 0);
    const hours = Number(match[2]);
    const minutes = Number(match[3]);
    if (days > 0) return `${days}d ${hours}h`;
    if (hours > 0) return `${hours}h ${minutes}m`;
    return `${minutes}m`;
};

const PartyChip: React.FC<{ party: string }> = ({ party }) => (
    <span
        className={`inline-flex items-center gap-1 px-2 py-0.5 rounded-md text-[10px] font-bold uppercase tracking-wider border ${
            party === 'Client'
                ? 'bg-amber-50 text-amber-700 border-amber-200'
                : 'bg-indigo-50 text-indigo-700 border-indigo-200'
        }`}
    >
        {party === 'Client' ? <User className="w-3 h-3" /> : <Users className="w-3 h-3" />}
        {party}
    </span>
);

const DueLabel: React.FC<{ item: NextActionItem }> = ({ item }) => {
    if (item.isOverdue) {
        const by = formatOverdueBy(item.overdueBy);
        return (
            <span className="inline-flex items-center gap-1 text-[11px] font-semibold text-rose-700">
                <AlertTriangle className="w-3.5 h-3.5" />
                Overdue{by ? ` by ${by}` : ''}
            </span>
        );
    }
    if (item.dueAtUtc) {
        return (
            <span className="inline-flex items-center gap-1 text-[11px] text-slate-500">
                <Clock className="w-3.5 h-3.5" />
                Due {new Date(item.dueAtUtc).toLocaleDateString()}
            </span>
        );
    }
    return null;
};

const ItemRow: React.FC<{ item: NextActionItem; index: number }> = ({ item, index }) => (
    <li className="flex items-start gap-3 py-2.5">
        <span className="mt-0.5 w-5 h-5 shrink-0 rounded-full bg-slate-100 text-slate-500 text-[10px] font-bold flex items-center justify-center">
            {index + 1}
        </span>
        <div className="min-w-0 flex-1">
            <div className="flex flex-wrap items-center gap-2">
                <span className="text-xs font-semibold text-slate-800">{item.title}</span>
                <PartyChip party={item.responsibleParty} />
                <DueLabel item={item} />
            </div>
            <p className="text-[11px] text-slate-500 mt-0.5 break-words">{item.reason}</p>
        </div>
    </li>
);

/**
 * CSTD-19 (19-N6): staff view of the deterministic next-action engine. The primary action, blockers
 * and gate reasons come straight from GET /next-action; nothing here decides priority. The Advance
 * button is enabled only when the engine's primary action is AdvanceStage, and PUT /stage still
 * re-checks the gate server-side.
 */
export const NextActionPanel: React.FC<NextActionPanelProps> = ({
    result,
    isLoading,
    error,
    isAdvancing,
    onAdvance,
    onRefresh,
}) => {
    const state = result ? STATE_STYLES[result.overallState] ?? { label: result.overallState, className: 'bg-slate-100 text-slate-700 border-slate-200' } : null;
    const primary = result?.primaryAction ?? null;
    const canAdvance = primary?.kind === 'AdvanceStage';
    const showAdvance = result && result.overallState !== 'Closed' && result.overallState !== 'AllComplete' && result.overallState !== 'NotStarted';
    const gateReasons = result?.nextStageGate && !result.nextStageGate.isSatisfied ? result.nextStageGate.reasons : [];

    return (
        <section className="bg-white border border-slate-200 rounded-2xl shadow-sm p-5 space-y-4" aria-label="Next action">
            <div className="flex flex-wrap items-center justify-between gap-3">
                <div className="flex flex-wrap items-center gap-2">
                    <h3 className="text-sm font-bold text-slate-900 uppercase tracking-wider">Next Action</h3>
                    {state && (
                        <span className={`px-2.5 py-0.5 rounded-full border text-[11px] font-semibold ${state.className}`}>
                            {state.label}
                        </span>
                    )}
                    {result?.isStalled && (
                        <span className="inline-flex items-center gap-1 px-2.5 py-0.5 rounded-full border border-rose-200 bg-rose-50 text-rose-700 text-[11px] font-semibold">
                            <PauseCircle className="w-3.5 h-3.5" /> Stalled
                        </span>
                    )}
                </div>
                <button
                    type="button"
                    onClick={onRefresh}
                    disabled={isLoading}
                    className="text-xs text-slate-500 hover:text-indigo-700 font-semibold flex items-center gap-1 disabled:opacity-50"
                >
                    <RefreshCw className={`w-3.5 h-3.5 ${isLoading ? 'animate-spin' : ''}`} />
                    Refresh
                </button>
            </div>

            {error && !result && (
                <div className="flex items-start gap-2 p-3 rounded-xl bg-rose-50 border border-rose-200 text-xs text-rose-800">
                    <WifiOff className="w-4 h-4 shrink-0" />
                    <span>Could not load the next action: {error}</span>
                </div>
            )}

            {isLoading && !result && !error && (
                <div className="flex items-center gap-2 text-xs text-slate-500">
                    <Loader2 className="w-4 h-4 animate-spin" /> Evaluating next action...
                </div>
            )}

            {result && (
                <>
                    {primary ? (
                        <div className="p-4 rounded-xl border border-indigo-100 bg-indigo-50/50">
                            <div className="flex flex-wrap items-center gap-2 mb-1.5">
                                <span className="text-[10px] font-bold uppercase tracking-wider text-indigo-600">
                                    Primary{primary.stageNumber ? ` • Stage ${primary.stageNumber}` : ''}
                                </span>
                                <PartyChip party={primary.responsibleParty} />
                                <DueLabel item={primary} />
                                {primary.priorityRank > 0 && (
                                    <span className="text-[10px] text-slate-400 font-semibold">Rule {primary.priorityRank}</span>
                                )}
                            </div>
                            <div className="text-sm font-bold text-slate-900">{primary.title}</div>
                            <p className="text-xs text-slate-600 mt-1">{primary.reason}</p>
                        </div>
                    ) : (
                        <div className="flex items-center gap-2 p-3 rounded-xl bg-slate-50 border border-slate-200 text-xs text-slate-600">
                            <CheckCircle2 className="w-4 h-4 text-emerald-600" />
                            No open action right now.
                        </div>
                    )}

                    {showAdvance && (
                        <div className="flex flex-wrap items-center justify-between gap-3">
                            <span className="text-[11px] text-slate-500">
                                {canAdvance
                                    ? 'All gates for the current stage are satisfied.'
                                    : `Advance is available once the primary action is "Advance stage" (${result.blockers.length + (primary ? 1 : 0)} open item${result.blockers.length + (primary ? 1 : 0) === 1 ? '' : 's'}).`}
                            </span>
                            <button
                                type="button"
                                onClick={onAdvance}
                                disabled={!canAdvance || isAdvancing}
                                className="px-4 py-2 rounded-xl text-xs font-bold flex items-center gap-2 transition shadow-sm bg-gradient-to-r from-[#635bff] to-[#712ae2] hover:opacity-95 text-white disabled:opacity-40 disabled:cursor-not-allowed"
                            >
                                {isAdvancing ? (
                                    <>
                                        <Loader2 className="w-4 h-4 animate-spin" /> Advancing...
                                    </>
                                ) : (
                                    <>
                                        Advance stage <ChevronRight className="w-4 h-4" />
                                    </>
                                )}
                            </button>
                        </div>
                    )}

                    {result.blockers.length > 0 && (
                        <div>
                            <h4 className="text-[11px] font-bold uppercase tracking-wider text-slate-500 flex items-center gap-1.5">
                                <ShieldAlert className="w-3.5 h-3.5" /> Blockers ({result.blockers.length})
                            </h4>
                            <ol className="divide-y divide-slate-100">
                                {result.blockers.map((b, i) => (
                                    <ItemRow key={`${b.kind}-${b.actionId ?? b.sourceId ?? b.title}-${i}`} item={b} index={i} />
                                ))}
                            </ol>
                        </div>
                    )}

                    {gateReasons.length > 0 && (
                        <div>
                            <h4 className="text-[11px] font-bold uppercase tracking-wider text-slate-500 flex items-center gap-1.5">
                                <ArrowRight className="w-3.5 h-3.5" /> Gate to {result.nextStageGate?.targetStage}
                            </h4>
                            <ul className="mt-1.5 space-y-1">
                                {gateReasons.map((reason, i) => (
                                    <li key={i} className="text-[11px] text-slate-600 flex items-start gap-1.5">
                                        <span className="mt-1.5 w-1 h-1 rounded-full bg-slate-400 shrink-0" />
                                        {reason}
                                    </li>
                                ))}
                            </ul>
                        </div>
                    )}

                    {result.upcomingConditions?.length > 0 && (
                        <div>
                            <h4 className="text-[11px] font-bold uppercase tracking-wider text-slate-400 flex items-center gap-1.5">
                                <Info className="w-3.5 h-3.5" /> Upcoming conditions (informational)
                            </h4>
                            <ol className="divide-y divide-slate-100">
                                {result.upcomingConditions.map((c, i) => (
                                    <ItemRow key={`${c.sourceId ?? c.title}-${i}`} item={c} index={i} />
                                ))}
                            </ol>
                        </div>
                    )}

                    <div className="text-[10px] text-slate-400">
                        Evaluated {new Date(result.evaluatedAtUtc).toLocaleTimeString()}
                    </div>
                </>
            )}
        </section>
    );
};

export default NextActionPanel;
