import React, { useState, useEffect, useCallback } from 'react';
import { Link } from 'react-router-dom';
import { useAuth } from '../context/AuthContext';
import { ClientPortalDashboard, ClientSafeAction, DocumentMetadata, ClientSafeCondition } from '../types';
import { PortalApi, WorkflowApi, DocumentsApi } from '../services/api';
import { UploadEvidenceModal } from './UploadEvidenceModal';
import { SubmitRequirementModal } from './SubmitRequirementModal';
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
    UploadCloud,
    Plus,
    FolderOpen,
    CreditCard,
    CheckSquare,
    DollarSign,
    XCircle,
    ShieldCheck
} from 'lucide-react';

interface ClientPortalViewProps {
    engagementId?: string;
}

export const ClientPortalView: React.FC<ClientPortalViewProps> = ({ engagementId: initialEngagementId }) => {
    const { tenantId, userId, tenantName, role } = useAuth();
    const [dashboard, setDashboard] = useState<ClientPortalDashboard | null>(null);
    const [conditions, setConditions] = useState<ClientSafeCondition[]>([]);
    const [loading, setLoading] = useState<boolean>(true);
    const [loadingConditions, setLoadingConditions] = useState<boolean>(false);
    const [error, setError] = useState<string | null>(null);
    const [actionInProgress, setActionInProgress] = useState<string | null>(null);
    const [selectedActionForUpload, setSelectedActionForUpload] = useState<ClientSafeAction | null>(null);
    const [selectedActionForRequirement, setSelectedActionForRequirement] = useState<ClientSafeAction | null>(null);
    const [documents, setDocuments] = useState<DocumentMetadata[]>([]);
    const [loadingDocs, setLoadingDocs] = useState<boolean>(false);
    const [isDirectUploadOpen, setIsDirectUploadOpen] = useState<boolean>(false);

    // CSTD-16: a Requirement-backed action must be submitted via SubmitRequirementModal (which
    // calls PUT /requirements/{id}/submit), never the generic complete/upload flow — the
    // presence of linkedRequirementId is the single source of truth for this, not the action's
    // Type string, since the backend enforces the same distinction server-side.
    const isRequirementAction = (action: ClientSafeAction): boolean =>
        Boolean(action.linkedRequirementId) || action.sourceType === 'Requirement';

    const isEvidenceAction = (action: ClientSafeAction): boolean => {
        if (action.sourceType === 'Document') return true;
        const type = (action.type || '').toLowerCase();
        // Every ActionType that requires an actual uploaded file, matched exactly first —
        // 'signagreement' (Signed Master Services Agreement) was previously missed here,
        // so that task fell through to the generic "Mark Done" bypass with no upload at all.
        if (
            type === 'documentupload' ||
            type === 'uploaddocument' ||
            type === 'kycdocument' ||
            type === 'signagreement' ||
            type === 'signdocument' ||
            type === 'proofofaddress'
        ) {
            return true;
        }
        const title = action.title.toLowerCase();
        return (
            type.includes('document') ||
            type.includes('upload') ||
            title.includes('upload') ||
            title.includes('document') ||
            title.includes('proof') ||
            title.includes('agreement') ||
            title.includes('contract') ||
            title.includes('signature') ||
            title.includes('evidence') ||
            title.includes('certificate') ||
            title.includes('passport')
        );
    };

    const fetchDocuments = useCallback(async (engId: string) => {
        if (!tenantId || !engId) return;
        setLoadingDocs(true);
        try {
            const docs = await DocumentsApi.getDocuments(engId, tenantId);
            setDocuments(docs || []);
        } catch (err) {
            console.error('Failed to load engagement documents:', err);
        } finally {
            setLoadingDocs(false);
        }
    }, [tenantId]);

    const fetchDashboard = useCallback(async () => {
        if (!tenantId) return;
        setLoading(true);
        setError(null);

        try {
            let data: ClientPortalDashboard;
            // Only a real Client caller's userId maps to a client identity. For Staff/Owner
            // previewing, sending their own staff userId as clientId makes the backend treat
            // them as a client trying to access someone else's engagement (ownership check
            // fails, request 404s) instead of triggering the actual staff-preview fallback.
            const effectiveClientId = role === 'Client' ? userId : undefined;
            if (initialEngagementId) {
                data = await PortalApi.getEngagementDashboard(initialEngagementId, tenantId, effectiveClientId);
            } else {
                // Auto-resolve active engagement for the authenticated client (Zero IDOR)
                data = await PortalApi.getMyEngagement(tenantId, effectiveClientId);
            }
            setDashboard(data);
            if (data?.engagementId) {
                await fetchDocuments(data.engagementId);
                try {
                    const conds = await WorkflowApi.getClientConditions(data.engagementId, tenantId);
                    setConditions(conds || []);
                } catch (cErr) {
                    console.warn('Failed to load client conditions:', cErr);
                }
            }
        } catch (err: any) {
            setError(err.message || 'Failed to load client onboarding portal');
        } finally {
            setLoading(false);
        }
    }, [tenantId, userId, initialEngagementId, fetchDocuments, role]);

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
                <div className="w-12 h-12 rounded-xl bg-indigo-50 border border-indigo-200 flex items-center justify-center mx-auto text-indigo-600">
                    <Layers className="w-6 h-6" />
                </div>
                <div>
                    <h3 className="text-base font-bold text-slate-900">
                        {role !== 'Client' ? 'No Engagements to Preview' : 'Unable to Load Client Portal'}
                    </h3>
                    <p className="text-xs text-slate-600 mt-1 max-w-md mx-auto leading-relaxed">
                        {role !== 'Client'
                            ? 'You are in Staff Preview Mode, but no client onboarding engagements exist in this workspace yet. Create an engagement from the agency dashboard first.'
                            : error || 'No active engagement was found for your client account in this workspace.'}
                    </p>
                </div>
                <div className="flex items-center justify-center gap-3">
                    {role !== 'Client' ? (
                        <Link
                            to="/engagements"
                            className="px-4 py-2 rounded-xl bg-gradient-to-r from-[#635bff] to-[#712ae2] text-white text-xs font-semibold hover:opacity-95 transition shadow-sm flex items-center gap-1.5"
                        >
                            <span>Go to Engagements Dashboard</span>
                            <ArrowRight className="w-3.5 h-3.5" />
                        </Link>
                    ) : (
                        <button
                            onClick={fetchDashboard}
                            className="px-4 py-2 rounded-xl bg-gradient-to-r from-[#635bff] to-[#712ae2] text-white text-xs font-semibold hover:opacity-95 transition"
                        >
                            Retry Connection
                        </button>
                    )}
                </div>
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
            {/* Staff Preview Mode Banner */}
            {role !== 'Client' && (
                <div className="bg-gradient-to-r from-indigo-50 to-violet-50 border border-indigo-200 p-3.5 rounded-2xl flex flex-col sm:flex-row sm:items-center justify-between gap-3 text-xs text-indigo-950 shadow-xs">
                    <div className="flex items-center gap-2.5">
                        <span className="font-bold uppercase tracking-wider bg-gradient-to-r from-[#635bff] to-[#712ae2] text-white px-2.5 py-0.5 rounded-full text-[10px] shadow-xs">
                            Staff Preview Mode
                        </span>
                        <span>
                            You are previewing how client stakeholders see this onboarding portal for <strong>ENG-{engagementId.slice(0, 6).toUpperCase()}</strong>.
                        </span>
                    </div>
                    <Link
                        to="/engagements"
                        className="font-semibold text-indigo-700 hover:text-indigo-900 transition flex items-center gap-1 shrink-0"
                    >
                        <span>← Back to All Engagements</span>
                    </Link>
                </div>
            )}

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

            {/* CSTD-24: Active Engagement Conditions (Approval & Payment Gates) */}
            {conditions.length > 0 && (
                <div className="bg-white/90 backdrop-blur-md p-6 rounded-2xl border border-slate-200/90 shadow-xs space-y-4">
                    <div className="flex flex-col sm:flex-row sm:items-center justify-between gap-3 pb-3 border-b border-slate-100">
                        <div className="flex items-center gap-3">
                            <div className="p-2.5 rounded-xl bg-violet-50 text-violet-700 border border-violet-100">
                                <ShieldCheck className="w-5 h-5" />
                            </div>
                            <div>
                                <div className="flex items-center gap-2">
                                    <h2 className="text-base font-bold text-slate-900">Milestone Conditions & Gate Requirements</h2>
                                    <span className="px-2.5 py-0.5 rounded-full text-[10px] font-bold bg-violet-100 text-violet-700">
                                        {conditions.length} Active Gate{conditions.length === 1 ? '' : 's'}
                                    </span>
                                </div>
                                <p className="text-xs text-slate-500">
                                    Prerequisite approval and payment conditions required before this engagement can advance to subsequent pipeline stages.
                                </p>
                            </div>
                        </div>
                    </div>

                    <div className="grid grid-cols-1 md:grid-cols-2 gap-3.5">
                        {conditions.map((cond) => {
                            const isPayment = cond.type === 'Payment';
                            const isSatisfied = cond.status === 'Satisfied';
                            const isRejected = cond.status === 'Rejected';

                            return (
                                <div
                                    key={cond.conditionId}
                                    className={`p-4 rounded-xl border transition space-y-2.5 ${
                                        isSatisfied
                                            ? 'bg-emerald-50/40 border-emerald-200'
                                            : isRejected
                                            ? 'bg-rose-50/40 border-rose-200'
                                            : 'bg-slate-50/60 border-slate-200/80 shadow-xs'
                                    }`}
                                >
                                    <div className="flex items-center justify-between gap-2">
                                        <div className="flex items-center gap-2">
                                            <span
                                                className={`inline-flex items-center gap-1 px-2.5 py-0.5 rounded-full text-[10px] font-bold ${
                                                    isPayment
                                                        ? 'bg-emerald-100 text-emerald-800'
                                                        : 'bg-indigo-100 text-indigo-800'
                                                }`}
                                            >
                                                {isPayment ? (
                                                    <CreditCard className="w-3 h-3" />
                                                ) : (
                                                    <CheckSquare className="w-3 h-3" />
                                                )}
                                                {cond.type} Gate
                                            </span>

                                            <span className="text-[10px] font-semibold px-2 py-0.5 rounded bg-white text-slate-700 border border-slate-200">
                                                Required before: {cond.requiredBeforeStage}
                                            </span>
                                        </div>

                                        <span
                                            className={`inline-flex items-center gap-1 px-2.5 py-0.5 rounded-full text-[10px] font-bold ${
                                                isSatisfied
                                                    ? 'bg-emerald-100 text-emerald-800'
                                                    : isRejected
                                                    ? 'bg-rose-100 text-rose-800'
                                                    : 'bg-amber-100 text-amber-800'
                                            }`}
                                        >
                                            {isSatisfied ? (
                                                <CheckCircle2 className="w-3 h-3" />
                                            ) : isRejected ? (
                                                <XCircle className="w-3 h-3" />
                                            ) : (
                                                <Clock className="w-3 h-3" />
                                            )}
                                            {cond.status}
                                        </span>
                                    </div>

                                    <div>
                                        <h3 className="text-sm font-bold text-slate-900">{cond.title}</h3>
                                        {cond.description && (
                                            <p className="text-xs text-slate-600 mt-1 leading-relaxed">
                                                {cond.description}
                                            </p>
                                        )}
                                    </div>

                                    {/* Payment Details */}
                                    {isPayment && (
                                        <div className="flex flex-wrap items-center gap-2.5 pt-1 text-xs">
                                            <span className="font-bold text-emerald-700 flex items-center gap-1">
                                                <DollarSign className="w-3.5 h-3.5" />
                                                {cond.amount?.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })} {cond.currency}
                                            </span>
                                            {cond.paymentType && (
                                                <span className="px-2 py-0.5 rounded bg-emerald-50 text-emerald-700 border border-emerald-200 font-semibold text-[10px]">
                                                    {cond.paymentType} Payment
                                                </span>
                                            )}
                                        </div>
                                    )}

                                    {/* Due Date & Overdue Tag */}
                                    <div className="flex items-center justify-between pt-1 border-t border-slate-200/60 text-[11px] text-slate-500">
                                        <div className="flex items-center gap-1.5">
                                            <Calendar className="w-3 h-3 text-slate-400" />
                                            <span>
                                                {cond.dueDateUtc
                                                    ? `Due: ${new Date(cond.dueDateUtc).toLocaleDateString()}`
                                                    : 'No strict due date'}
                                            </span>
                                        </div>

                                        {cond.isOverdue && cond.status === 'Pending' && (
                                            <span className="px-2 py-0.5 rounded-full text-[10px] font-bold bg-rose-100 text-rose-700 border border-rose-200 animate-pulse">
                                                Action Overdue
                                            </span>
                                        )}
                                    </div>

                                    <div className="pt-1 text-[10px] text-slate-400 italic">
                                        Read-only milestone requirement. Processed in coordination with your Custodian representative.
                                    </div>
                                </div>
                            );
                        })}
                    </div>
                </div>
            )}

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
                            {primaryNextAction.sourceType && (
                                <span className="text-xs font-semibold px-2 py-0.5 rounded bg-indigo-50 text-indigo-700 border border-indigo-100">
                                    {primaryNextAction.sourceType}
                                </span>
                            )}
                            {primaryNextAction.verificationStatus && (
                                <span
                                    className={`text-xs font-semibold px-2.5 py-0.5 rounded-full border ${
                                        primaryNextAction.verificationStatus === 'Verified'
                                            ? 'bg-emerald-50 text-emerald-700 border-emerald-200'
                                            : primaryNextAction.verificationStatus === 'Rejected'
                                            ? 'bg-rose-50 text-rose-700 border-rose-200'
                                            : 'bg-sky-50 text-sky-700 border-sky-200'
                                    }`}
                                >
                                    {primaryNextAction.verificationStatus}
                                </span>
                            )}
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

                    {/* Revision Alert Banner if action was rejected by staff or compliance engine */}
                    {(primaryNextAction.status === 'Rejected' || primaryNextAction.verificationStatus === 'Rejected') && (
                        <div className="p-3 bg-amber-50 border border-amber-200 rounded-xl flex items-start gap-2.5 text-xs text-amber-900">
                            <AlertTriangle className="w-4 h-4 text-amber-600 shrink-0 mt-0.5" />
                            <div>
                                <strong className="font-semibold text-amber-950">Revision Requested: </strong>
                                {primaryNextAction.rejectionReason
                                    ? primaryNextAction.rejectionReason
                                    : 'Custodian verification flagged previous evidence as incomplete or invalid. Please upload an updated PDF document.'}
                            </div>
                        </div>
                    )}

                    <div className="pt-3 flex items-center justify-between">
                        <span className="text-[11px] text-slate-500">
                            Required to clear the Stage {primaryNextAction.stageNumber} milestone gate.
                        </span>
                        <div className="flex items-center gap-2">
                            {isRequirementAction(primaryNextAction) ? (
                                // CSTD-16: Requirement-backed actions submit through their own
                                // modal/endpoint — never the generic complete flow (see
                                // ClientActionService.CompleteActionAsync's guard).
                                <button
                                    onClick={() => setSelectedActionForRequirement(primaryNextAction)}
                                    className="px-5 py-2.5 rounded-xl bg-gradient-to-r from-[#635bff] to-[#712ae2] hover:opacity-95 text-white text-xs font-bold flex items-center gap-2 shadow-sm shadow-indigo-500/25 transition cursor-pointer"
                                >
                                    <span>{primaryNextAction.status === 'Rejected' ? 'Re-submit Information' : 'Submit Information'}</span>
                                    <ArrowRight className="w-4 h-4" />
                                </button>
                            ) : isEvidenceAction(primaryNextAction) ? (
                                // Evidence/document actions (KYC, signed agreements, uploads) can ONLY be
                                // completed via the real upload -> compliance -> staff-verification pipeline
                                // (CSTD-18 gate) — no "mark done without uploading" escape hatch, since that
                                // would let a client skip document verification entirely and still trigger
                                // the stage auto-advance.
                                <button
                                    onClick={() => setSelectedActionForUpload(primaryNextAction)}
                                    className="px-5 py-2.5 rounded-xl bg-gradient-to-r from-[#635bff] to-[#712ae2] hover:opacity-95 text-white text-xs font-bold flex items-center gap-2 shadow-sm shadow-indigo-500/25 transition cursor-pointer"
                                >
                                    <UploadCloud className="w-4 h-4" />
                                    <span>{primaryNextAction.status === 'Rejected' ? 'Re-upload Evidence' : 'Upload Evidence'}</span>
                                </button>
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
                    <div className="pt-2">
                        <button
                            onClick={() => setIsDirectUploadOpen(true)}
                            className="inline-flex items-center gap-2 px-4 py-2 rounded-xl bg-emerald-600 hover:bg-emerald-700 text-white text-xs font-bold transition shadow-xs cursor-pointer"
                        >
                            <UploadCloud className="w-4 h-4" />
                            <span>Upload Additional Evidence</span>
                        </button>
                    </div>
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
                                        {action.sourceType && (
                                            <span className="text-[10px] font-semibold px-1.5 py-0.5 rounded bg-indigo-50 text-indigo-700 border border-indigo-100">
                                                {action.sourceType}
                                            </span>
                                        )}
                                        {action.verificationStatus && (
                                            <span
                                                className={`text-[10px] font-semibold px-2 py-0.5 rounded-full border ${
                                                    action.verificationStatus === 'Verified'
                                                        ? 'bg-emerald-50 text-emerald-700 border-emerald-200'
                                                        : action.verificationStatus === 'Rejected'
                                                        ? 'bg-rose-50 text-rose-700 border-rose-200'
                                                        : 'bg-sky-50 text-sky-700 border-sky-200'
                                                }`}
                                            >
                                                {action.verificationStatus}
                                            </span>
                                        )}
                                    </div>
                                    <p className="text-[11px] text-slate-500 line-clamp-1">{action.description || 'Upcoming onboarding requirement'}</p>
                                    {action.rejectionReason && (
                                        <p className="text-[11px] text-amber-700 font-medium">
                                            Reason: {action.rejectionReason}
                                        </p>
                                    )}
                                </div>

                                <div className="flex items-center gap-2 shrink-0">
                                    {action.deadlineUtc && (
                                        <span className="text-[11px] font-semibold text-slate-600 flex items-center gap-1 mr-1">
                                            <Calendar className="w-3 h-3 text-slate-400" />
                                            {new Date(action.deadlineUtc).toLocaleDateString()}
                                        </span>
                                    )}
                                    {isRequirementAction(action) ? (
                                        <button
                                            onClick={() => setSelectedActionForRequirement(action)}
                                            className="px-3 py-1.5 rounded-lg bg-indigo-50 hover:bg-indigo-100 border border-indigo-200 text-indigo-700 text-xs font-semibold transition flex items-center gap-1.5 cursor-pointer"
                                        >
                                            <span>Submit</span>
                                        </button>
                                    ) : isEvidenceAction(action) ? (
                                        // No "Mark Done" bypass for evidence actions — see the
                                        // primary-action panel above for why.
                                        <button
                                            onClick={() => setSelectedActionForUpload(action)}
                                            className="px-3 py-1.5 rounded-lg bg-indigo-50 hover:bg-indigo-100 border border-indigo-200 text-indigo-700 text-xs font-semibold transition flex items-center gap-1.5 cursor-pointer"
                                        >
                                            <UploadCloud className="w-3.5 h-3.5" />
                                            <span>Upload</span>
                                        </button>
                                    ) : (
                                        <button
                                            onClick={() => handleCompleteAction(action)}
                                            disabled={actionInProgress === action.actionId}
                                            className="px-3.5 py-1.5 rounded-lg bg-white hover:bg-slate-50 border border-slate-200 text-slate-600 text-xs font-semibold transition cursor-pointer"
                                        >
                                            {actionInProgress === action.actionId ? 'Completing...' : 'Mark Done'}
                                        </button>
                                    )}
                                </div>
                            </div>
                        ))}
                    </div>
                </div>
            )}

            {/* Dedicated Document Vault & Compliance Evidence Section */}
            <div className="bg-white/90 backdrop-blur-md p-6 rounded-2xl border border-slate-200/90 shadow-xs space-y-4">
                <div className="flex flex-col sm:flex-row sm:items-center justify-between gap-3 pb-3 border-b border-slate-100">
                    <div className="flex items-center gap-3">
                        <div className="w-10 h-10 rounded-xl bg-indigo-50 border border-indigo-100 flex items-center justify-center text-indigo-600">
                            <FolderOpen className="w-5 h-5" />
                        </div>
                        <div>
                            <div className="flex items-center gap-2">
                                <h3 className="text-sm font-bold text-slate-900">Compliance Document Vault</h3>
                                <span className="text-[10px] font-bold px-2 py-0.5 rounded-full bg-slate-100 text-slate-600 border border-slate-200">
                                    {documents.length} {documents.length === 1 ? 'file' : 'files'}
                                </span>
                            </div>
                            <p className="text-xs text-slate-400">
                                Secure repository of your identity verification, legal agreements, and compliance records.
                            </p>
                        </div>
                    </div>
                    <button
                        onClick={() => setIsDirectUploadOpen(true)}
                        className="px-4 py-2 rounded-xl bg-gradient-to-r from-[#635bff] to-[#712ae2] hover:opacity-95 text-white text-xs font-bold flex items-center gap-2 shadow-sm shadow-indigo-500/25 transition cursor-pointer self-start sm:self-auto"
                    >
                        <Plus className="w-4 h-4" />
                        <span>Upload New Document</span>
                    </button>
                </div>

                {loadingDocs ? (
                    <div className="p-8 text-center space-y-2">
                        <Loader2 className="w-6 h-6 text-indigo-600 animate-spin mx-auto" />
                        <p className="text-xs text-slate-400">Loading uploaded vault records...</p>
                    </div>
                ) : documents.length === 0 ? (
                    <div
                        onClick={() => setIsDirectUploadOpen(true)}
                        className="border-2 border-dashed border-slate-200 hover:border-indigo-300 hover:bg-slate-50/50 rounded-2xl p-8 text-center transition cursor-pointer space-y-2.5"
                    >
                        <div className="w-10 h-10 rounded-xl bg-slate-100 text-slate-500 flex items-center justify-center mx-auto">
                            <UploadCloud className="w-5 h-5" />
                        </div>
                        <h4 className="text-xs font-bold text-slate-700">No documents uploaded yet</h4>
                        <p className="text-[11px] text-slate-400 max-w-sm mx-auto">
                            Upload your passport, government ID, proof of address, or signed contract directly to the engagement vault.
                        </p>
                        <span className="inline-block text-xs font-semibold text-indigo-600 hover:underline pt-1">
                            Click here to upload evidence
                        </span>
                    </div>
                ) : (
                    <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
                        {documents.map((doc) => (
                            <div
                                key={doc.documentId}
                                className="p-4 rounded-xl bg-slate-50/70 border border-slate-200 hover:border-slate-300 transition space-y-2.5"
                            >
                                <div className="flex items-start justify-between gap-2">
                                    <div className="flex items-start gap-2.5 min-w-0">
                                        <div className="w-8 h-8 rounded-lg bg-white border border-slate-200 flex items-center justify-center text-slate-600 shrink-0 mt-0.5">
                                            <FileText className="w-4 h-4" />
                                        </div>
                                        <div className="min-w-0">
                                            <div className="text-xs font-bold text-slate-900 truncate">
                                                {doc.fileName || doc.type}
                                            </div>
                                            <div className="text-[10px] text-slate-400">
                                                Type: <span className="font-semibold text-slate-600">{doc.type}</span>
                                            </div>
                                        </div>
                                    </div>
                                    <div className="flex flex-col items-end gap-1 shrink-0">
                                        {doc.complianceStatus && (
                                            <span
                                                className={`text-[9px] font-bold px-2 py-0.5 rounded-full border ${
                                                    doc.complianceStatus === 'Compliant'
                                                        ? 'bg-emerald-50 text-emerald-700 border-emerald-200'
                                                        : doc.complianceStatus === 'Rejected'
                                                        ? 'bg-rose-50 text-rose-700 border-rose-200'
                                                        : 'bg-amber-50 text-amber-700 border-amber-200'
                                                }`}
                                            >
                                                {doc.complianceStatus}
                                            </span>
                                        )}
                                        {doc.verificationStatus && (
                                            <span
                                                className={`text-[9px] font-bold px-2 py-0.5 rounded-full border ${
                                                    doc.verificationStatus === 'Verified'
                                                        ? 'bg-indigo-50 text-indigo-700 border-indigo-200'
                                                        : doc.verificationStatus === 'Rejected'
                                                        ? 'bg-rose-50 text-rose-700 border-rose-200'
                                                        : 'bg-sky-50 text-sky-700 border-sky-200'
                                                }`}
                                            >
                                                {doc.verificationStatus}
                                            </span>
                                        )}
                                    </div>
                                </div>

                                <div className="flex items-center justify-between text-[11px] text-slate-500 pt-1 border-t border-slate-200/60">
                                    <span className="flex items-center gap-1">
                                        <Calendar className="w-3 h-3 text-slate-400" />
                                        {doc.issueDate ? `Issued: ${new Date(doc.issueDate).toLocaleDateString()}` : `Uploaded: ${new Date(doc.uploadedAt).toLocaleDateString()}`}
                                    </span>
                                    {doc.expiryDate && (
                                        <span className="text-[10px] text-slate-400">
                                            Expires: {new Date(doc.expiryDate).toLocaleDateString()}
                                        </span>
                                    )}
                                </div>

                                {doc.rejectionReason && (
                                    <div className="p-2 bg-rose-50 border border-rose-200 rounded-lg text-[11px] text-rose-700 flex items-start gap-1.5">
                                        <AlertCircle className="w-3.5 h-3.5 text-rose-600 shrink-0 mt-0.5" />
                                        <span>Rejection Reason: {doc.rejectionReason}</span>
                                    </div>
                                )}
                            </div>
                        ))}
                    </div>
                )}
            </div>

            {/* CSTD-16: Requirement submission modal */}
            <SubmitRequirementModal
                isOpen={Boolean(selectedActionForRequirement)}
                action={selectedActionForRequirement}
                onClose={() => setSelectedActionForRequirement(null)}
                engagementId={dashboard.engagementId}
                tenantId={tenantId || ''}
                userId={userId}
                onSuccess={fetchDashboard}
            />

            {/* Action-bound Evidence Upload Modal */}
            <UploadEvidenceModal
                isOpen={Boolean(selectedActionForUpload)}
                action={selectedActionForUpload}
                onClose={() => setSelectedActionForUpload(null)}
                engagementId={dashboard.engagementId}
                tenantId={tenantId || ''}
                userId={userId}
                onSuccess={() => {
                    fetchDashboard();
                    if (dashboard?.engagementId) fetchDocuments(dashboard.engagementId);
                }}
            />

            {/* Direct Document Vault Upload Modal */}
            <UploadEvidenceModal
                isOpen={isDirectUploadOpen}
                action={null}
                onClose={() => setIsDirectUploadOpen(false)}
                engagementId={dashboard.engagementId}
                tenantId={tenantId || ''}
                userId={userId}
                onSuccess={() => {
                    fetchDashboard();
                    if (dashboard?.engagementId) fetchDocuments(dashboard.engagementId);
                }}
            />
        </div>
    );
};
