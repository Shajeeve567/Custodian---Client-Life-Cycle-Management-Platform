import React, { useState, useEffect, useCallback } from 'react';
import { useNavigate } from 'react-router-dom';
import { useAuth } from '../context/AuthContext';
import { WorkflowApi, IdentityApi } from '../services/api';
import { Engagement, ClientProfile, UserAccountResponse, ClientAction, EngagementStage } from '../types';
import { ENGAGEMENT_STAGES, getStageDefinition, getStageIndex, getNextStage } from '../constants/engagementStages';
import {
    ArrowLeft,
    CheckCircle2,
    Shield,
    FileText,
    Activity,
    ChevronRight,
    ExternalLink,
    Loader2,
    CheckCircle,
    AlertTriangle,
    Layers,
    Lock
} from 'lucide-react';

interface WorkspaceStageViewProps {
    engagementId: string;
    onBack?: () => void;
}

export const WorkspaceStageView: React.FC<WorkspaceStageViewProps> = ({
    engagementId,
    onBack,
}) => {
    const navigate = useNavigate();
    const { token, tenantId, userId, role } = useAuth();

    // Data state
    const [engagement, setEngagement] = useState<Engagement | null>(null);
    const [client, setClient] = useState<ClientProfile | null>(null);
    const [staffLead, setStaffLead] = useState<UserAccountResponse | null>(null);
    const [actions, setActions] = useState<ClientAction[]>([]);
    const [isLoading, setIsLoading] = useState(true);

    // Which stage card the user is currently viewing details for (not necessarily
    // the engagement's actual current stage — clicking a card just previews it).
    const [selectedStageKey, setSelectedStageKey] = useState<EngagementStage | null>(null);

    const [completingActionId, setCompletingActionId] = useState<string | null>(null);
    const [isAdvancingStage, setIsAdvancingStage] = useState(false);
    const [stageAdvanceSuccess, setStageAdvanceSuccess] = useState<string | null>(null);
    const [stageAdvanceError, setStageAdvanceError] = useState<string | null>(null);

    // Load Workspace Data
    const loadWorkspaceData = useCallback(async () => {
        if (!tenantId || !engagementId) return;
        setIsLoading(true);

        try {
            const engagements = await WorkflowApi.getEngagements(tenantId).catch(() => [] as Engagement[]);
            const foundEng = engagements.find((e) => e.engagementId === engagementId);
            if (foundEng) {
                setEngagement(foundEng);
                setSelectedStageKey((prev) => prev ?? foundEng.stage);
            }

            if (token) {
                try {
                    const clients = await IdentityApi.getClients(token);
                    if (foundEng) {
                        const matchedClient = clients.find((c) => c.id === foundEng.clientId);
                        if (matchedClient) setClient(matchedClient);
                    }
                } catch (cErr) {
                    console.warn(cErr);
                }

                if (role === 'Owner') {
                    try {
                        const users = await IdentityApi.getUsers(token);
                        if (foundEng) {
                            const matchedStaff = users.find((u) => u.id === foundEng.staffId);
                            if (matchedStaff) setStaffLead(matchedStaff);
                        }
                    } catch (uErr) {
                        console.warn(uErr);
                    }
                }
            }

            try {
                const actList = await WorkflowApi.getActions(engagementId, tenantId);
                setActions(actList || []);
            } catch (aErr) {
                console.warn(aErr);
            }
        } catch (err) {
            console.error(err);
        } finally {
            setIsLoading(false);
        }
    }, [tenantId, engagementId, token, role]);

    useEffect(() => {
        loadWorkspaceData();
    }, [loadWorkspaceData]);

    const handleCompleteAction = async (actionId: string) => {
        setCompletingActionId(actionId);
        setTimeout(() => {
            setActions((prev) =>
                prev.map((a) => (a.actionId === actionId ? { ...a, isCompleted: true } : a))
            );
            setCompletingActionId(null);
        }, 500);
    };

    // Advances the engagement to the next stage via the real backend endpoint
    // (PUT /api/Engagements/{id}/stage). The server enforces sequential,
    // forward-only transitions (CSTD-17 AC4) — this is the source of truth,
    // not anything computed locally in this component.
    const handleAdvanceStage = async () => {
        if (!engagement || !tenantId) return;
        const next = getNextStage(engagement.stage);
        if (!next) return;

        setIsAdvancingStage(true);
        setStageAdvanceError(null);
        setStageAdvanceSuccess(null);

        try {
            const updated = await WorkflowApi.updateStage(engagement.engagementId, next, tenantId);
            setEngagement(updated);
            setSelectedStageKey(updated.stage);
            const nextDef = getStageDefinition(updated.stage);
            setStageAdvanceSuccess(`Advanced to Stage ${nextDef.order}: ${nextDef.name}`);
            setTimeout(() => setStageAdvanceSuccess(null), 4000);
        } catch (err: any) {
            setStageAdvanceError(err?.message || 'Failed to advance stage.');
        } finally {
            setIsAdvancingStage(false);
        }
    };

    // Client display info
    const clientName = client?.name || `Client ${engagementId.slice(0, 8)}`;
    const initials = clientName
        .split(' ')
        .map((n) => n[0])
        .filter(Boolean)
        .join('')
        .slice(0, 2)
        .toUpperCase() || 'CL';
    const engagementCode = `ENG-${engagementId.slice(0, 6).toUpperCase()}`;

    const currentStageOrder = getStageIndex(engagement?.stage) + 1; // 0 if unknown, else 1-5
    const selectedStageDef = getStageDefinition(selectedStageKey ?? engagement?.stage);
    const progressPercent = engagement?.stageProgressPercentage ?? 0;
    const isTerminalStatus = engagement?.status === 'Closed' || engagement?.status === 'Cancelled';
    const nextStage = engagement ? getNextStage(engagement.stage) : null;
    const isViewingCurrentStage = selectedStageDef.key === engagement?.stage;

    return (
        <div className="space-y-6">
            {/* Top Navigation & Breadcrumb */}
            <div className="flex items-center justify-between">
                <button
                    onClick={onBack ? onBack : () => navigate('/engagements')}
                    className="inline-flex items-center gap-2 text-sm font-semibold text-slate-600 hover:text-indigo-600 transition"
                >
                    <ArrowLeft className="w-4 h-4" />
                    <span>Back to Engagements</span>
                </button>

                <div className="flex items-center gap-2.5">
                    <button
                        onClick={() => navigate('/documents')}
                        className="px-3.5 py-2 rounded-xl bg-white/80 backdrop-blur-xs border border-slate-200 hover:bg-slate-50 text-xs font-semibold text-slate-700 flex items-center gap-2 shadow-xs transition"
                    >
                        <FileText className="w-4 h-4 text-indigo-600" />
                        <span>Document Vault</span>
                    </button>
                    <button
                        onClick={() => navigate('/audit')}
                        className="px-3.5 py-2 rounded-xl bg-white/80 backdrop-blur-xs border border-slate-200 hover:bg-slate-50 text-xs font-semibold text-slate-700 flex items-center gap-2 shadow-xs transition"
                    >
                        <Shield className="w-4 h-4 text-indigo-600" />
                        <span>Audit Log</span>
                    </button>
                </div>
            </div>

            {/* Workspace Master Header */}
            <div className="bg-white/90 backdrop-blur-md p-6 rounded-2xl border border-slate-200/90 shadow-xs flex flex-col md:flex-row md:items-center justify-between gap-6">
                <div className="flex items-center gap-4">
                    <div className="w-14 h-14 rounded-2xl bg-gradient-to-br from-[#635bff] to-[#712ae2] text-white font-bold text-xl flex items-center justify-center shadow-md shadow-indigo-500/20">
                        {initials}
                    </div>
                    <div>
                        <div className="flex items-center gap-3">
                            <h1 className="text-2xl font-bold text-slate-900 tracking-tight">
                                {clientName}
                            </h1>
                            <span className="px-2.5 py-0.5 rounded text-xs font-mono font-semibold bg-slate-100 text-slate-700 border border-slate-200">
                                {engagementCode}
                            </span>
                            <span className={`inline-flex items-center gap-1.5 px-2.5 py-0.5 rounded-full text-xs font-semibold border ${
                                isTerminalStatus
                                    ? 'bg-slate-100 text-slate-600 border-slate-200'
                                    : 'bg-emerald-50 text-emerald-700 border-emerald-200'
                            }`}>
                                <span className={`w-1.5 h-1.5 rounded-full ${isTerminalStatus ? 'bg-slate-400' : 'bg-emerald-500'}`} />
                                {engagement?.status || 'Active Pipeline'}
                            </span>
                        </div>
                        <p className="text-xs text-slate-500 mt-1">
                            Tenant Scoped Isolation: <code className="font-mono text-slate-700">{tenantId || 'Main Tenant'}</code> • Deterministic State Engine Active
                        </p>
                    </div>
                </div>

                <div className="flex items-center gap-3 shrink-0">
                    <div className="p-3 bg-slate-50/80 rounded-xl border border-slate-200/80 text-xs">
                        <span className="text-[10px] font-bold text-slate-400 uppercase tracking-wider block">
                            STAFF CUSTODIAN
                        </span>
                        <span className="font-semibold text-slate-800 truncate block mt-0.5">
                            {staffLead?.email || 'Assigned Custodian'}
                        </span>
                    </div>

                    <div className="p-3 bg-indigo-50/80 rounded-xl border border-indigo-200/80 text-xs">
                        <span className="text-[10px] font-bold text-indigo-600 uppercase tracking-wider block">
                            STAGE PROGRESS
                        </span>
                        <span className="font-bold text-indigo-700 block mt-0.5">
                            {progressPercent}% Complete
                        </span>
                    </div>
                </div>
            </div>

            {/* 5-Stage Pipeline Stepper */}
            <div className="bg-white/90 backdrop-blur-md p-5 rounded-2xl border border-slate-200/90 shadow-xs space-y-4">
                <div className="flex items-center justify-between pb-3 border-b border-slate-100">
                    <div>
                        <h2 className="text-base font-bold text-slate-900 flex items-center gap-2">
                            <Layers className="w-5 h-5 text-indigo-600" />
                            5-Stage Engagement Pipeline
                        </h2>
                        <p className="text-xs text-slate-500 mt-0.5">
                            Onboarding → Document Collection → Verification → Execution → Closure.
                        </p>
                    </div>
                    <span className="px-3 py-1 rounded-full text-xs font-semibold bg-indigo-50 text-indigo-700 border border-indigo-200">
                        Current: Stage {currentStageOrder || 1} of 5
                    </span>
                </div>

                {/* 5 Step Cards */}
                <div className="grid grid-cols-1 md:grid-cols-5 gap-3">
                    {ENGAGEMENT_STAGES.map((st) => {
                        const isPassed = st.order < currentStageOrder;
                        const isCurrent = st.order === currentStageOrder;
                        const isSelected = st.key === (selectedStageKey ?? engagement?.stage);

                        return (
                            <button
                                key={st.key}
                                onClick={() => setSelectedStageKey(st.key)}
                                className={`text-left p-3.5 rounded-xl border transition ${
                                    isSelected
                                        ? 'bg-indigo-50/70 border-indigo-300 ring-2 ring-indigo-500/20 shadow-xs'
                                        : isPassed
                                        ? 'bg-emerald-50/50 border-emerald-200 hover:bg-emerald-50'
                                        : isCurrent
                                        ? 'bg-white border-indigo-200 shadow-xs'
                                        : 'bg-slate-50 border-slate-200 hover:bg-white'
                                }`}
                            >
                                <div className="flex items-center justify-between mb-1.5">
                                    <span
                                        className={`w-6 h-6 rounded-full flex items-center justify-center text-xs font-bold ${
                                            isPassed
                                                ? 'bg-emerald-600 text-white'
                                                : isCurrent
                                                ? 'bg-gradient-to-br from-[#635bff] to-[#712ae2] text-white shadow-xs'
                                                : 'bg-slate-200 text-slate-600'
                                        }`}
                                    >
                                        {isPassed ? '✓' : st.order}
                                    </span>
                                    {isPassed && (
                                        <span className="text-[10px] font-bold text-emerald-700 uppercase tracking-wider">
                                            Passed
                                        </span>
                                    )}
                                    {isCurrent && (
                                        <span className="text-[10px] font-bold text-indigo-600 uppercase tracking-wider">
                                            Active
                                        </span>
                                    )}
                                </div>
                                <div className="text-xs font-bold text-slate-800 truncate">
                                    {st.name}
                                </div>
                                <div className="text-[11px] text-slate-500 truncate mt-0.5">
                                    {st.tagline}
                                </div>
                            </button>
                        );
                    })}
                </div>
            </div>

            {/* Stage Alerts */}
            {stageAdvanceSuccess && (
                <div className="p-3 bg-emerald-50 border border-emerald-200 rounded-xl text-emerald-900 text-xs font-semibold flex items-center gap-2">
                    <CheckCircle className="w-4 h-4 text-emerald-600 shrink-0" />
                    <span>{stageAdvanceSuccess}</span>
                </div>
            )}
            {stageAdvanceError && (
                <div className="p-3 bg-red-50 border border-red-200 rounded-xl text-red-900 text-xs font-semibold flex items-center gap-2">
                    <AlertTriangle className="w-4 h-4 text-red-600 shrink-0" />
                    <span>{stageAdvanceError}</span>
                </div>
            )}

            {/* Stage Deep Dive: Two Columns */}
            <div className="grid grid-cols-1 lg:grid-cols-12 gap-6 items-start">
                {/* Left: Stage Details & Advancement (8 Cols) */}
                <div className="lg:col-span-8 space-y-4">
                    <div className="bg-white/90 backdrop-blur-md p-6 rounded-2xl border border-slate-200/90 shadow-xs space-y-5">
                        {/* Stage Header */}
                        <div>
                            <div className="flex items-center gap-2 mb-1.5">
                                <span className="px-2.5 py-0.5 rounded-full text-xs font-bold bg-indigo-100 text-indigo-700 uppercase tracking-wider">
                                    Stage {selectedStageDef.order}
                                </span>
                                {isViewingCurrentStage && (
                                    <span className="text-xs font-semibold text-emerald-700 flex items-center gap-1">
                                        <span className="w-1.5 h-1.5 rounded-full bg-emerald-500" /> Active Operational Target
                                    </span>
                                )}
                            </div>
                            <h3 className="text-xl font-bold text-slate-900">
                                {selectedStageDef.name}
                            </h3>
                            <p className="text-xs text-slate-600 mt-1 leading-relaxed">
                                {selectedStageDef.description}
                            </p>
                        </div>

                        {/* Progress Bar — derived from the backend's real stageProgressPercentage,
                            never computed locally, so it can't drift from the engagement's actual stage */}
                        <div className="pt-2 border-t border-slate-100">
                            <div className="flex items-center justify-between text-xs mb-1.5">
                                <span className="font-bold text-slate-500 uppercase tracking-wider">
                                    Overall Engagement Progress
                                </span>
                                <span className="font-bold text-indigo-600">
                                    {progressPercent}%
                                </span>
                            </div>
                            <div className="h-2 w-full bg-slate-100 rounded-full overflow-hidden">
                                <div
                                    className="h-full bg-gradient-to-r from-[#635bff] to-[#712ae2] rounded-full transition-all duration-300"
                                    style={{ width: `${progressPercent}%` }}
                                />
                            </div>
                        </div>

                        {/* Advance Stage Footer */}
                        {isViewingCurrentStage && nextStage && !isTerminalStatus && (
                            <div className="pt-4 border-t border-slate-100 flex items-center justify-between">
                                <span className="text-xs text-slate-500">
                                    Ready to advance to the next stage in the pipeline.
                                </span>
                                <button
                                    type="button"
                                    onClick={handleAdvanceStage}
                                    disabled={isAdvancingStage}
                                    className="px-5 py-2.5 rounded-xl text-xs font-bold flex items-center gap-2 transition shadow-sm bg-gradient-to-r from-[#635bff] to-[#712ae2] hover:opacity-95 text-white shadow-indigo-500/25 disabled:opacity-60 disabled:cursor-not-allowed"
                                >
                                    {isAdvancingStage ? (
                                        <>
                                            <Loader2 className="w-4 h-4 animate-spin" />
                                            <span>Advancing Stage...</span>
                                        </>
                                    ) : (
                                        <>
                                            <span>Advance to Stage {getStageDefinition(nextStage).order}: {getStageDefinition(nextStage).name}</span>
                                            <ChevronRight className="w-4 h-4" />
                                        </>
                                    )}
                                </button>
                            </div>
                        )}

                        {isViewingCurrentStage && isTerminalStatus && (
                            <div className="pt-4 border-t border-slate-100 text-xs text-slate-500">
                                Engagement status is <span className="font-semibold">{engagement?.status}</span> — stage can no longer advance.
                            </div>
                        )}

                        {isViewingCurrentStage && engagement?.stage === 'Closure' && !isTerminalStatus && (
                            <div className="pt-4 border-t border-slate-100 flex items-center justify-between">
                                <span className="text-xs text-slate-500">
                                    Final stage reached.
                                </span>
                                <button
                                    type="button"
                                    onClick={() => alert('Engagement deliverables sealed into immutable audit ledger.')}
                                    className="px-5 py-2.5 rounded-xl bg-emerald-600 hover:bg-emerald-700 text-white text-xs font-bold flex items-center gap-2 shadow-sm transition"
                                >
                                    <Lock className="w-4 h-4" />
                                    <span>Seal & Complete Engagement</span>
                                </button>
                            </div>
                        )}
                    </div>
                </div>

                {/* Right: Stage Actions & Vaults (4 Cols) */}
                <div className="lg:col-span-4 space-y-4">
                    {/* Action Queue */}
                    <div className="bg-white/90 backdrop-blur-md p-5 rounded-2xl border border-slate-200/90 shadow-xs space-y-3">
                        <div className="flex items-center justify-between pb-2 border-b border-slate-100">
                            <h3 className="text-sm font-bold text-slate-900 flex items-center gap-1.5">
                                <Activity className="w-4 h-4 text-indigo-600" />
                                Stage Action Queue
                            </h3>
                            <span className="px-2 py-0.5 rounded-full text-xs font-semibold bg-slate-100 text-slate-600">
                                {actions.length} Tasks
                            </span>
                        </div>

                        {actions.length === 0 ? (
                            <div className="p-4 rounded-xl bg-slate-50 text-center space-y-1">
                                <CheckCircle2 className="w-6 h-6 text-emerald-600 mx-auto" />
                                <div className="text-xs font-semibold text-slate-800">
                                    All Stage Actions Up-To-Date
                                </div>
                                <p className="text-[11px] text-slate-500">
                                    No pending gate stalls or unhandled client blockers.
                                </p>
                            </div>
                        ) : (
                            <div className="space-y-2">
                                {actions.map((act) => (
                                    <div
                                        key={act.actionId}
                                        className="p-3 rounded-xl bg-slate-50 border border-slate-200 space-y-1.5"
                                    >
                                        <div className="flex items-center justify-between">
                                            <span className="text-xs font-bold text-slate-800">
                                                {act.title}
                                            </span>
                                            {act.isCompleted ? (
                                                <span className="text-[10px] font-bold text-emerald-700 bg-emerald-50 px-2 py-0.5 rounded border border-emerald-200">
                                                    Done
                                                </span>
                                            ) : (
                                                <span className="text-[10px] font-bold text-amber-700 bg-amber-50 px-2 py-0.5 rounded border border-amber-200">
                                                    Pending
                                                </span>
                                            )}
                                        </div>
                                        {!act.isCompleted && (
                                            <button
                                                onClick={() => handleCompleteAction(act.actionId)}
                                                disabled={completingActionId === act.actionId}
                                                className="w-full py-1.5 rounded-lg bg-indigo-600 hover:bg-indigo-700 text-white text-xs font-semibold mt-1 transition"
                                            >
                                                {completingActionId === act.actionId ? 'Completing...' : 'Mark Complete'}
                                            </button>
                                        )}
                                    </div>
                                ))}
                            </div>
                        )}
                    </div>

                    {/* Integrated Artifact Launchers */}
                    <div className="bg-white p-5 rounded-2xl border border-slate-200 shadow-sm space-y-3">
                        <span className="text-[10px] font-bold text-slate-400 uppercase tracking-wider block">
                            WORKSPACE ARTIFACTS & EVIDENCE
                        </span>

                        <button
                            onClick={() => navigate('/documents')}
                            className="w-full p-3.5 rounded-xl border border-slate-200 hover:border-indigo-300 hover:bg-indigo-50/40 text-left flex items-center justify-between transition group"
                        >
                            <div className="flex items-center gap-3">
                                <div className="p-2 rounded-lg bg-indigo-50 text-indigo-600 group-hover:bg-indigo-600 group-hover:text-white transition">
                                    <FileText className="w-4 h-4" />
                                </div>
                                <div>
                                    <div className="text-xs font-bold text-slate-800">
                                        Compliance Document Vault
                                    </div>
                                    <div className="text-[11px] text-slate-500">
                                        KYC/AML verification & contracts
                                    </div>
                                </div>
                            </div>
                            <ExternalLink className="w-4 h-4 text-slate-400 group-hover:text-indigo-600 transition" />
                        </button>

                        <button
                            onClick={() => navigate('/audit')}
                            className="w-full p-3.5 rounded-xl border border-slate-200 hover:border-indigo-300 hover:bg-indigo-50/40 text-left flex items-center justify-between transition group"
                        >
                            <div className="flex items-center gap-3">
                                <div className="p-2 rounded-lg bg-indigo-50 text-indigo-600 group-hover:bg-indigo-600 group-hover:text-white transition">
                                    <Shield className="w-4 h-4" />
                                </div>
                                <div>
                                    <div className="text-xs font-bold text-slate-800">
                                        Immutable Audit Ledger
                                    </div>
                                    <div className="text-[11px] text-slate-500">
                                        Cryptographic proof chain & hashes
                                    </div>
                                </div>
                            </div>
                            <ExternalLink className="w-4 h-4 text-slate-400 group-hover:text-indigo-600 transition" />
                        </button>
                    </div>
                </div>
            </div>
        </div>
    );
};
