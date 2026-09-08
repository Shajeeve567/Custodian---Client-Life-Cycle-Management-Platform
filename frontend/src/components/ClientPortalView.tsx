import React, { useState, useEffect, useCallback } from 'react';
import { useAuth } from '../context/AuthContext';
import { ClientPortalDashboard, ClientSafeAction } from '../types';
import { PortalApi, WorkflowApi } from '../services/api';
import { UploadEvidenceModal } from './UploadEvidenceModal';
import {
    Shield,
    CheckCircle2,
    Clock,
    AlertCircle,
    AlertTriangle,
    Calendar,
    Layers,
    FileText,
    ArrowRight,
    Loader2,
    RefreshCw,
    Sparkles,
    Check,
    UploadCloud
} from 'lucide-react';

interface ClientPortalViewProps {
    engagementId?: string;
}

export const ClientPortalView: React.FC<ClientPortalViewProps> = ({ engagementId: initialEngagementId }) => {
    const { tenantId, userId, tenantName } = useAuth();
    const [dashboard, setDashboard] = useState<ClientPortalDashboard | null>(null);
    const [loading, setLoading] = useState<boolean>(true);
    const [error, setError] = useState<string | null>(null);
    const [actionInProgress, setActionInProgress] = useState<string | null>(null);
    const [selectedActionForUpload, setSelectedActionForUpload] = useState<ClientSafeAction | null>(null);

    const isEvidenceAction = (action: ClientSafeAction): boolean => {
        const type = (action.type || '').toLowerCase();
        const title = action.title.toLowerCase();
        return (
            type === 'documentupload' ||
            type === 'kycdocument' ||
            type.includes('document') ||
            type.includes('upload') ||
            title.includes('upload') ||
            title.includes('document') ||
            title.includes('proof') ||
            title.includes('id') ||
            title.includes('evidence') ||
            title.includes('certificate') ||
            title.includes('passport')
        );
    };

    const fetchDashboard = useCallback(async () => {
        if (!tenantId) return;
        setLoading(true);
        setError(null);

        try {
            let data: ClientPortalDashboard;
            if (initialEngagementId) {
                data = await PortalApi.getEngagementDashboard(initialEngagementId, tenantId, userId);
            } else {
                // Auto-resolve active engagement for the authenticated client (Zero IDOR)
                data = await PortalApi.getMyEngagement(tenantId, userId);
            }
            setDashboard(data);
        } catch (err: any) {
            setError(err.message || 'Failed to load client onboarding portal');
        } finally {
            setLoading(false);
        }
    }, [tenantId, userId, initialEngagementId]);

    useEffect(() => {
        fetchDashboard();
    }, [fetchDashboard]);

    const handleCompleteAction = async (action: ClientSafeAction) => {
        if (!dashboard) return;
        setActionInProgress(action.actionId);

        try {
            await WorkflowApi.completeAction(
                action.actionId,
                tenantId || '',
                dashboard.engagementId,
                userId || 'client-user'
            );
            // Re-fetch live aggregated dashboard
            await fetchDashboard();
        } catch (err: any) {
            alert('Error updating action: ' + (err.message || 'Failed to complete task'));
        } finally {
            setActionInProgress(null);
        }
    };

    const renderConditionBadge = (status: string) => {
        switch (status) {
            case 'ActionRequired':
                return (
                    <span className="inline-flex items-center gap-1.5 px-3 py-1 rounded-full text-xs font-semibold bg-amber-50 text-amber-800 border border-amber-200">
                        <AlertCircle className="w-3.5 h-3.5 text-amber-600" />
                        Action Required
                    </span>
                );
            case 'UnderReview':
                return (
                    <span className="inline-flex items-center gap-1.5 px-3 py-1 rounded-full text-xs font-semibold bg-sky-50 text-sky-800 border border-sky-200 animate-pulse">
                        <Clock className="w-3.5 h-3.5 text-sky-600" />
                        Under Review by Custodian
                    </span>
                );
            case 'Overdue':
                return (
                    <span className="inline-flex items-center gap-1.5 px-3 py-1 rounded-full text-xs font-semibold bg-rose-50 text-rose-800 border border-rose-200">
                        <AlertTriangle className="w-3.5 h-3.5 text-rose-600" />
                        Overdue Action
                    </span>
                );
            case 'RevisionRequired':
                return (
                    <span className="inline-flex items-center gap-1.5 px-3 py-1 rounded-full text-xs font-semibold bg-orange-50 text-orange-800 border border-orange-200">
                        <RefreshCw className="w-3.5 h-3.5 text-orange-600" />
                        Revision Requested
                    </span>
                );
            case 'Closed':
                return (
                    <span className="inline-flex items-center gap-1.5 px-3 py-1 rounded-full text-xs font-semibold bg-indigo-50 text-indigo-800 border border-indigo-200">
                        <Check className="w-3.5 h-3.5 text-indigo-600" />
                        Engagement Delivered & Sealed
                    </span>
                );
            case 'AllCaughtUp':
            default:
                return (
                    <span className="inline-flex items-center gap-1.5 px-3 py-1 rounded-full text-xs font-semibold bg-emerald-50 text-emerald-800 border border-emerald-200">
                        <CheckCircle2 className="w-3.5 h-3.5 text-emerald-600" />
                        Stage Milestone Satisfied
                    </span>
                );
        }
    };

    if (loading) {
        return (
            <div className="p-12 text-center bg-white/80 backdrop-blur-md rounded-2xl border border-slate-200/80 shadow-xs space-y-3">
                <Loader2 className="w-8 h-8 text-indigo-600 animate-spin mx-auto" />
                <h3 className="text-sm font-semibold text-slate-700">Loading your onboarding portal...</h3>
                <p className="text-xs text-slate-400">Aggregating live stage progression and active next actions.</p>
            </div>
        );
    }

    if (error || !dashboard) {
        return (
            <div className="p-8 bg-white/90 backdrop-blur-md rounded-2xl border border-slate-200 shadow-xs text-center space-y-4">
                <div className="w-12 h-12 rounded-xl bg-amber-50 border border-amber-200 flex items-center justify-center mx-auto text-amber-600">
                    <AlertCircle className="w-6 h-6" />
                </div>
                <div>
                    <h3 className="text-base font-bold text-slate-900">Unable to Load Client Portal</h3>
                    <p className="text-xs text-slate-600 mt-1 max-w-md mx-auto">
                        {error || 'No active engagement was found for your client account in this workspace.'}
                    </p>
                </div>
                <button
                    onClick={fetchDashboard}
                    className="px-4 py-2 rounded-xl bg-gradient-to-r from-[#635bff] to-[#712ae2] text-white text-xs font-semibold hover:opacity-95 transition"
                >
                    Retry Connection
                </button>
            </div>
        );
    }

    const {
        engagementId,
        status,
        currentStageNumber,
        currentStageName,
        currentStageTagline,
        conditionStatus,
        conditionDescription,
        progressPercentage,
        completedTasksCount,
        totalTasksCount,
        primaryNextAction,
        pendingActions,
        stages,
    } = dashboard;

    return (
        <div className="space-y-6">
            {/* Master Header Card */}
            <div className="bg-white/90 backdrop-blur-md p-6 rounded-2xl border border-slate-200/90 shadow-xs">
                <div className="flex flex-col md:flex-row md:items-center justify-between gap-4">
                    <div className="flex items-center gap-4">
                        <div className="w-13 h-13 rounded-2xl bg-gradient-to-br from-[#635bff] to-[#712ae2] text-white font-bold flex items-center justify-center shadow-md shadow-indigo-500/20">
                            <Shield className="w-6 h-6 text-white" />
                        </div>
                        <div>
                            <div className="flex items-center gap-3">
                                <h1 className="text-2xl font-bold text-slate-900 tracking-tight">
                                    Client Onboarding Portal
                                </h1>
                                <span className="px-2.5 py-0.5 rounded text-xs font-mono font-semibold bg-slate-100 text-slate-700 border border-slate-200">
                                    ENG-{engagementId.slice(0, 6).toUpperCase()}
                                </span>
                                <span className="px-2.5 py-0.5 rounded-full text-xs font-semibold bg-indigo-50 text-indigo-700 border border-indigo-200">
                                    Status: {status}
                                </span>
                            </div>
                            <p className="text-xs text-slate-500 mt-1">
                                Workspace: <span className="font-semibold text-slate-700">{tenantName || 'Main Workspace'}</span> • Live Workflow Synchronized
                            </p>
                        </div>
                    </div>

                    <div className="flex items-center gap-3">
                        {renderConditionBadge(conditionStatus)}
                    </div>
                </div>

                {/* Milestone Condition Banner */}
                {conditionDescription && (
                    <div className="mt-4 p-3.5 rounded-xl bg-slate-50/80 border border-slate-200/80 flex items-start gap-2.5 text-xs text-slate-700">
                        <Clock className="w-4 h-4 text-indigo-600 shrink-0 mt-0.5" />
                        <span className="leading-relaxed">
                            <strong className="text-slate-900 font-semibold">Active Condition: </strong>
                            {conditionDescription}
                        </span>
                    </div>
                )}
            </div>

            {/* 5-Stage Stepper Roadmap */}
            <div className="bg-white/90 backdrop-blur-md p-5 rounded-2xl border border-slate-200/90 shadow-xs space-y-3">
                <div className="flex items-center justify-between pb-2 border-b border-slate-100">
                    <div className="flex items-center gap-2">
                        <Layers className="w-4 h-4 text-indigo-600" />
                        <h2 className="text-sm font-bold text-slate-900">Onboarding Lifecycle Roadmap</h2>
                    </div>
                    <span className="text-xs font-semibold text-indigo-600">
                        Stage {currentStageNumber} of 5: {currentStageName}
                    </span>
                </div>

                <div className="grid grid-cols-1 md:grid-cols-5 gap-3 pt-1">
                    {stages.map((st) => {
                        const isCompleted = st.status === 'Completed';
                        const isCurrent = st.status === 'Current';

                        return (
                            <div
                                key={st.stageNumber}
                                className={`p-3 rounded-xl border transition ${
                                    isCurrent
                                        ? 'bg-indigo-50/70 border-indigo-300 ring-2 ring-indigo-500/20 shadow-xs'
                                        : isCompleted
                                        ? 'bg-emerald-50/50 border-emerald-200'
                                        : 'bg-slate-50/70 border-slate-200 opacity-60'
                                }`}
                            >
                                <div className="flex items-center justify-between mb-1">
                                    <span
                                        className={`w-5 h-5 rounded-full flex items-center justify-center text-[10px] font-bold ${
                                            isCompleted
                                                ? 'bg-emerald-600 text-white'
                                                : isCurrent
                                                ? 'bg-gradient-to-br from-[#635bff] to-[#712ae2] text-white shadow-xs'
                                                : 'bg-slate-200 text-slate-600'
                                        }`}
                                    >
                                        {isCompleted ? '✓' : st.stageNumber}
                                    </span>
                                    <span
                                        className={`text-[9px] font-bold uppercase tracking-wider ${
                                            isCompleted
                                                ? 'text-emerald-700'
                                                : isCurrent
                                                ? 'text-indigo-600'
                                                : 'text-slate-400'
                                        }`}
                                    >
                                        {st.status}
                                    </span>
                                </div>
                                <div className="text-xs font-bold text-slate-800 truncate">{st.name}</div>
                                <div className="text-[10px] text-slate-500 truncate mt-0.5">{st.tagline}</div>
                            </div>
                        );
                    })}
                </div>
            </div>

            {/* Total Tasks Progress Meter */}
            <div className="bg-white/90 backdrop-blur-md p-5 rounded-2xl border border-slate-200/90 shadow-xs space-y-2.5">
                <div className="flex items-center justify-between text-xs">
                    <span className="font-bold text-slate-600 uppercase tracking-wider">
                        Total Task Progress
                    </span>
                    <span className="font-bold text-indigo-600 text-sm">
                        {completedTasksCount} / {totalTasksCount} Tasks Completed ({progressPercentage}%)
                    </span>
                </div>
                <div className="h-2.5 w-full bg-slate-100 rounded-full overflow-hidden">
                    <div
                        className="h-full bg-gradient-to-r from-[#635bff] to-[#712ae2] rounded-full transition-all duration-500"
                        style={{ width: `${progressPercentage}%` }}
                    />
                </div>
            </div>

            {/* HERO SECTION: Primary Next Action */}
            {primaryNextAction ? (
                <div className="relative overflow-hidden bg-gradient-to-br from-white via-indigo-50/20 to-violet-50/30 p-6 rounded-2xl border-2 border-indigo-200 shadow-md shadow-indigo-500/5 space-y-4">
                    <div className="flex flex-col sm:flex-row sm:items-center justify-between gap-2 pb-3 border-b border-indigo-100">
                        <div className="flex items-center gap-2">
                            <span className="px-3 py-1 rounded-full text-[11px] font-bold bg-gradient-to-r from-[#635bff] to-[#712ae2] text-white uppercase tracking-wider shadow-xs flex items-center gap-1.5">
                                <Sparkles className="w-3.5 h-3.5" />
                                Primary Next Action • Stage {primaryNextAction.stageNumber}
                            </span>
                            <span className="text-xs font-semibold px-2 py-0.5 rounded bg-white text-slate-700 border border-slate-200">
                                {primaryNextAction.type}
                            </span>
                        </div>

                        {/* Deadline Tag */}
                        {primaryNextAction.deadlineUtc ? (
                            <div
                                className={`inline-flex items-center gap-1.5 text-xs font-semibold px-3 py-1 rounded-full border ${
                                    primaryNextAction.isOverdue
                                        ? 'bg-rose-50 text-rose-700 border-rose-200 animate-pulse'
                                        : primaryNextAction.daysRemaining !== null && primaryNextAction.daysRemaining <= 3
                                        ? 'bg-amber-50 text-amber-700 border-amber-200'
                                        : 'bg-slate-50 text-slate-700 border-slate-200'
                                }`}
                            >
                                <Calendar className="w-3.5 h-3.5" />
                                <span>
                                    {primaryNextAction.isOverdue
                                        ? 'Overdue'
                                        : primaryNextAction.daysRemaining !== null
                                        ? `Due in ${primaryNextAction.daysRemaining} day${primaryNextAction.daysRemaining === 1 ? '' : 's'}`
                                        : 'Deadline set'}
                                    {' • '}
                                    {new Date(primaryNextAction.deadlineUtc).toLocaleDateString()}
                                </span>
                            </div>
                        ) : (
                            <span className="text-xs text-slate-400">No strict deadline</span>
                        )}
                    </div>

                    <div className="space-y-1.5">
                        <h2 className="text-xl font-bold text-slate-900">{primaryNextAction.title}</h2>
                        <p className="text-xs text-slate-600 leading-relaxed max-w-3xl">
                            {primaryNextAction.description ||
                                'This action item is required to fulfill stage verification criteria. Complete this task to advance to the next onboarding stage.'}
                        </p>
                    </div>

                    {/* Revision Alert Banner if action was rejected by staff */}
                    {primaryNextAction.status === 'Rejected' && (
                        <div className="p-3 bg-amber-50 border border-amber-200 rounded-xl flex items-start gap-2.5 text-xs text-amber-900">
                            <AlertTriangle className="w-4 h-4 text-amber-600 shrink-0 mt-0.5" />
                            <div>
                                <strong className="font-semibold text-amber-950">Revision Requested: </strong>
                                Custodian verification flagged previous evidence as incomplete or invalid. Please upload an updated PDF document.
                            </div>
                        </div>
                    )}

                    <div className="pt-3 flex items-center justify-between">
                        <span className="text-[11px] text-slate-500">
                            Required to clear the Stage {primaryNextAction.stageNumber} milestone gate.
                        </span>
                        <div className="flex items-center gap-2">
                            {isEvidenceAction(primaryNextAction) ? (
                                <>
                                    <button
                                        onClick={() => setSelectedActionForUpload(primaryNextAction)}
                                        className="px-5 py-2.5 rounded-xl bg-gradient-to-r from-[#635bff] to-[#712ae2] hover:opacity-95 text-white text-xs font-bold flex items-center gap-2 shadow-sm shadow-indigo-500/25 transition cursor-pointer"
                                    >
                                        <UploadCloud className="w-4 h-4" />
                                        <span>{primaryNextAction.status === 'Rejected' ? 'Re-upload Evidence' : 'Upload Evidence'}</span>
                                    </button>
                                    <button
                                        onClick={() => handleCompleteAction(primaryNextAction)}
                                        disabled={actionInProgress === primaryNextAction.actionId}
                                        className="px-3 py-2.5 rounded-xl border border-slate-200 hover:bg-slate-50 text-slate-600 text-xs font-semibold transition cursor-pointer"
                                        title="Mark task completed without file upload"
                                    >
                                        {actionInProgress === primaryNextAction.actionId ? (
                                            <Loader2 className="w-3.5 h-3.5 animate-spin" />
                                        ) : (
                                            'Mark Done'
                                        )}
                                    </button>
                                </>
                            ) : (
                                <button
                                    onClick={() => handleCompleteAction(primaryNextAction)}
                                    disabled={actionInProgress === primaryNextAction.actionId}
                                    className="px-5 py-2.5 rounded-xl bg-gradient-to-r from-[#635bff] to-[#712ae2] hover:opacity-95 text-white text-xs font-bold flex items-center gap-2 shadow-sm shadow-indigo-500/25 transition cursor-pointer"
                                >
                                    {actionInProgress === primaryNextAction.actionId ? (
                                        <>
                                            <Loader2 className="w-4 h-4 animate-spin" />
                                            <span>Processing...</span>
                                        </>
                                    ) : (
                                        <>
                                            <span>Complete Action</span>
                                            <ArrowRight className="w-4 h-4" />
                                        </>
                                    )}
                                </button>
                            )}
                        </div>
                    </div>
                </div>
            ) : conditionStatus === 'UnderReview' ? (
                <div className="bg-sky-50/70 border border-sky-200 p-8 rounded-2xl text-center space-y-3 shadow-xs">
                    <div className="w-12 h-12 rounded-xl bg-sky-100 flex items-center justify-center mx-auto text-sky-700">
                        <Clock className="w-6 h-6 animate-pulse" />
                    </div>
                    <h3 className="text-lg font-bold text-sky-950">Evidence Under Review</h3>
                    <p className="text-xs text-sky-800 max-w-md mx-auto">
                        Your submitted documents have been received and are undergoing review by our compliance team. You will be notified once verified.
                    </p>
                </div>
            ) : (
                <div className="bg-emerald-50/70 border border-emerald-200 p-8 rounded-2xl text-center space-y-3 shadow-xs">
                    <div className="w-12 h-12 rounded-xl bg-emerald-100 flex items-center justify-center mx-auto text-emerald-700">
                        <CheckCircle2 className="w-6 h-6" />
                    </div>
                    <h3 className="text-lg font-bold text-emerald-950">All Caught Up for Stage {currentStageNumber}!</h3>
                    <p className="text-xs text-emerald-800 max-w-md mx-auto">
                        You have completed all pending action items for this stage. Custodian staff is conducting milestone evaluation.
                    </p>
                </div>
            )}

            {/* Other Pending Actions (if any) */}
            {pendingActions.length > 0 && (
                <div className="bg-white/90 backdrop-blur-md p-5 rounded-2xl border border-slate-200/90 shadow-xs space-y-3">
                    <div className="flex items-center justify-between pb-2 border-b border-slate-100">
                        <div className="flex items-center gap-2">
                            <FileText className="w-4 h-4 text-indigo-600" />
                            <h3 className="text-sm font-bold text-slate-900">Upcoming Stage Tasks</h3>
                        </div>
                        <span className="text-xs text-slate-500 font-semibold">
                            {pendingActions.length} Pending
                        </span>
                    </div>

                    <div className="space-y-2 pt-1">
                        {pendingActions.map((action) => (
                            <div
                                key={action.actionId}
                                className="p-3.5 rounded-xl bg-slate-50 border border-slate-200 flex flex-col sm:flex-row sm:items-center justify-between gap-3"
                            >
                                <div className="space-y-1">
                                    <div className="flex items-center gap-2">
                                        <span className="text-xs font-bold text-slate-900">{action.title}</span>
                                        <span className="text-[10px] font-semibold px-2 py-0.5 rounded bg-white text-slate-600 border border-slate-200">
                                             Stage {action.stageNumber}
                                        </span>
                                    </div>
                                    <p className="text-[11px] text-slate-500 line-clamp-1">{action.description || 'Upcoming onboarding requirement'}</p>
                                </div>

                                <div className="flex items-center gap-2 shrink-0">
                                    {action.deadlineUtc && (
                                        <span className="text-[11px] font-semibold text-slate-600 flex items-center gap-1 mr-1">
                                            <Calendar className="w-3 h-3 text-slate-400" />
                                            {new Date(action.deadlineUtc).toLocaleDateString()}
                                        </span>
                                    )}
                                    {isEvidenceAction(action) && (
                                        <button
                                            onClick={() => setSelectedActionForUpload(action)}
                                            className="px-3 py-1.5 rounded-lg bg-indigo-50 hover:bg-indigo-100 border border-indigo-200 text-indigo-700 text-xs font-semibold transition flex items-center gap-1.5 cursor-pointer"
                                        >
                                            <UploadCloud className="w-3.5 h-3.5" />
                                            <span>Upload</span>
                                        </button>
                                    )}
                                    <button
                                        onClick={() => handleCompleteAction(action)}
                                        disabled={actionInProgress === action.actionId}
                                        className="px-3.5 py-1.5 rounded-lg bg-white hover:bg-slate-50 border border-slate-200 text-slate-600 text-xs font-semibold transition cursor-pointer"
                                    >
                                        {actionInProgress === action.actionId ? 'Completing...' : 'Mark Done'}
                                    </button>
                                </div>
                            </div>
                        ))}
                    </div>
                </div>
            )}

            {/* Evidence Upload Modal */}
            <UploadEvidenceModal
                isOpen={Boolean(selectedActionForUpload)}
                action={selectedActionForUpload}
                onClose={() => setSelectedActionForUpload(null)}
                engagementId={dashboard.engagementId}
                tenantId={tenantId || ''}
                userId={userId}
                onSuccess={fetchDashboard}
            />
        </div>
    );
};
