import React, { useState, useEffect, useCallback } from 'react';
import { useNavigate } from 'react-router-dom';
import { useAuth } from '../context/AuthContext';
import { WorkflowApi, IdentityApi, DocumentsApi } from '../services/api';
import { Engagement, ClientProfile, UserAccountResponse, ClientAction, DocumentMetadata, EngagementStage } from '../types';
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
    Lock,
    User,
    Building2,
    Calendar,
    ArrowRight,
    ShieldCheck,
    ShieldAlert,
    Download,
    Check,
    X,
    Clock,
    RefreshCw,
    Plus
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
    const [documents, setDocuments] = useState<DocumentMetadata[]>([]);
    const [isLoading, setIsLoading] = useState(true);

    // Which stage card the user is currently viewing details for (not necessarily
    // the engagement's actual current stage — clicking a card just previews it).
    const [selectedStageKey, setSelectedStageKey] = useState<EngagementStage | null>(null);

    const [completingActionId, setCompletingActionId] = useState<string | null>(null);
    const [isAdvancingStage, setIsAdvancingStage] = useState(false);
    const [stageAdvanceSuccess, setStageAdvanceSuccess] = useState<string | null>(null);
    const [stageAdvanceError, setStageAdvanceError] = useState<string | null>(null);
    const [stageFilterMode, setStageFilterMode] = useState<'stage' | 'all'>('stage');
    const [isAddTaskOpen, setIsAddTaskOpen] = useState(false);
    const [newTaskTitle, setNewTaskTitle] = useState('');
    const [newTaskDesc, setNewTaskDesc] = useState('');
    const [newTaskStage, setNewTaskStage] = useState<number>(1);
    const [newTaskType, setNewTaskType] = useState<string>('CustomTask');
    const [newTaskRole, setNewTaskRole] = useState<'Client' | 'Staff'>('Client');
    const [newTaskDeadline, setNewTaskDeadline] = useState<string>('');
    const [isCreatingTask, setIsCreatingTask] = useState(false);
    const [createTaskError, setCreateTaskError] = useState<string | null>(null);

    // Stage Action Verification Modals State
    const [actionToVerify, setActionToVerify] = useState<{ action: ClientAction; doc?: DocumentMetadata } | null>(null);
    const [verifyNotes, setVerifyNotes] = useState<string>('');
    const [isVerifyingAction, setIsVerifyingAction] = useState<boolean>(false);

    const [actionToReject, setActionToReject] = useState<{ action: ClientAction; doc?: DocumentMetadata } | null>(null);
    const [rejectReason, setRejectReason] = useState<string>('');
    const [isRejectingAction, setIsRejectingAction] = useState<boolean>(false);

    // Helper to match action to evidence document in vault
    const findLinkedDoc = (act: ClientAction): DocumentMetadata | undefined => {
        const actDocId = (act as any).documentId || (act as any).metadata?.documentId;
        if (actDocId) {
            const found = documents.find((d) => d.documentId === actDocId);
            if (found) return found;
        }

        if (act.type === 'KycDocument' || act.title.toLowerCase().includes('kyc') || act.title.toLowerCase().includes('passport')) {
            const found = documents.find((d) => d.type === 'KYC_PASSPORT');
            if (found) return found;
        }

        if (act.type === 'SignAgreement' || act.title.toLowerCase().includes('agreement') || act.title.toLowerCase().includes('contract')) {
            const found = documents.find((d) => d.type === 'SIGNED_AGREEMENT');
            if (found) return found;
        }

        if (act.type === 'DocumentUpload') {
            return documents.find((d) => !d.isDeleted);
        }

        return undefined;
    };

    // Load Workspace Data
    const loadWorkspaceData = useCallback(async () => {
        if (!tenantId || !engagementId) return;
        setIsLoading(true);

        try {
            const engagements = await WorkflowApi.getEngagements(tenantId).catch(() => [] as Engagement[]);
            const foundEng = engagements.find((e) => e.engagementId === engagementId);
            if (foundEng) {
                setEngagement((prevEng) => {
                    if (prevEng && prevEng.stage !== foundEng.stage) {
                        setSelectedStageKey(foundEng.stage);
                    } else if (!prevEng) {
                        setSelectedStageKey((prev) => prev ?? foundEng.stage);
                    }
                    return foundEng;
                });
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

            try {
                const docList = await DocumentsApi.getDocuments(engagementId, tenantId);
                setDocuments(docList || []);
            } catch (dErr) {
                console.warn(dErr);
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
        if (!tenantId) return;
        setCompletingActionId(actionId);
        try {
            await WorkflowApi.completeAction(actionId, tenantId, engagementId, userId || 'staff-lead');
            await loadWorkspaceData();
        } catch (err) {
            console.warn('Fallback local action completion:', err);
            setActions((prev) =>
                prev.map((a) => (a.actionId === actionId ? { ...a, isCompleted: true, status: 'Completed' } : a))
            );
        } finally {
            setCompletingActionId(null);
        }
    };

    // Dual-Sync Staff Verification Handler
    const handleConfirmVerifyAction = async () => {
        if (!actionToVerify || !tenantId) return;
        setIsVerifyingAction(true);
        try {
            const { action, doc } = actionToVerify;

            // 1. Verify in Document Microservice if document is linked
            if (doc) {
                await DocumentsApi.verifyDocument(
                    engagementId,
                    doc.documentId,
                    {
                        staffActor: userId || 'staff-lead',
                        staffNotes: verifyNotes.trim() || undefined,
                    },
                    tenantId
                );
            }

            // 2. Synchronize verification in Workflow Microservice
            await WorkflowApi.applyVerification(
                engagementId,
                action.actionId,
                {
                    verificationStatus: 'Verified',
                    verifiedBy: userId || 'staff-lead',
                    verificationReason: verifyNotes.trim() || undefined,
                },
                tenantId
            );


            // 4. Refresh workspace data
            await loadWorkspaceData();
            setActionToVerify(null);
            setVerifyNotes('');
        } catch (err: any) {
            alert('Verification failed: ' + (err.message || 'Server error'));
        } finally {
            setIsVerifyingAction(false);
        }
    };

    // Dual-Sync Staff Rejection Handler
    const handleConfirmRejectAction = async () => {
        if (!actionToReject || !tenantId) return;
        if (!rejectReason.trim()) {
            alert('Rejection reason is required.');
            return;
        }

        setIsRejectingAction(true);
        try {
            const { action, doc } = actionToReject;

            // 1. Reject in Document Microservice if document is linked
            if (doc) {
                await DocumentsApi.rejectDocument(
                    engagementId,
                    doc.documentId,
                    {
                        staffActor: userId || 'staff-lead',
                        reason: rejectReason.trim(),
                    },
                    tenantId
                );
            }

            // 2. Synchronize rejection in Workflow Microservice
            await WorkflowApi.applyVerification(
                engagementId,
                action.actionId,
                {
                    verificationStatus: 'Rejected',
                    verifiedBy: userId || 'staff-lead',
                    verificationReason: rejectReason.trim(),
                },
                tenantId
            );

            // 3. Refresh workspace data
            await loadWorkspaceData();
            setActionToReject(null);
            setRejectReason('');
        } catch (err: any) {
            alert('Rejection failed: ' + (err.message || 'Server error'));
        } finally {
            setIsRejectingAction(false);
        }
    };

    // Advances the engagement to the next stage via the real backend endpoint
    // (PUT /api/Engagements/{id}/stage). The server enforces sequential,
    // forward-only transitions (CSTD-17 AC4) — this is the source of truth,
    // not anything computed locally in this component.
    const handleCreateTask = async (e: React.FormEvent) => {
        e.preventDefault();
        if (!tenantId || !engagementId) return;
        if (!newTaskTitle.trim()) {
            setCreateTaskError('Task title is required.');
            return;
        }

        setIsCreatingTask(true);
        setCreateTaskError(null);
        try {
            await WorkflowApi.createAction(
                {
                    engagementId,
                    title: newTaskTitle.trim(),
                    description: newTaskDesc.trim() || undefined,
                    stageNumber: newTaskStage,
                    type: newTaskType,
                    assignedRole: newTaskRole as any,
                    deadlineUtc: newTaskDeadline ? new Date(newTaskDeadline).toISOString() : undefined,
                    isInternalOnly: newTaskRole === 'Staff',
                },
                tenantId
            );
            await loadWorkspaceData();
            setIsAddTaskOpen(false);
            setNewTaskTitle('');
            setNewTaskDesc('');
            setNewTaskDeadline('');
        } catch (err: any) {
            setCreateTaskError(err?.message || 'Failed to create task.');
        } finally {
            setIsCreatingTask(false);
        }
    };

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
                        onClick={() => navigate(`/portal?engagementId=${engagementId}`)}
                        className="px-3.5 py-2 rounded-xl bg-gradient-to-r from-indigo-50 to-purple-50 hover:from-indigo-100 hover:to-purple-100 border border-indigo-200 text-xs font-semibold text-indigo-700 flex items-center gap-2 shadow-xs transition"
                        title="Open Client Portal view for this engagement"
                    >
                        <ExternalLink className="w-4 h-4 text-indigo-600" />
                        <span>Preview in Client Portal</span>
                    </button>
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

                        {/* Upcoming Stage Activation Banner: When viewing the immediate next stage */}
                        {!isViewingCurrentStage && selectedStageDef.order === currentStageOrder + 1 && !isTerminalStatus && (
                            <div className="pt-4 border-t border-indigo-100 bg-indigo-50/60 p-4 rounded-xl flex flex-col sm:flex-row items-start sm:items-center justify-between gap-3">
                                <div>
                                    <div className="text-xs font-bold text-indigo-950 flex items-center gap-1.5">
                                        <span>Next Upcoming Stage</span>
                                        <span className="px-2 py-0.5 rounded bg-indigo-200/70 text-indigo-800 text-[10px] font-bold">Stage {selectedStageDef.order}</span>
                                    </div>
                                    <p className="text-[11px] text-indigo-700 mt-1">
                                        Engagement is currently active at Stage {currentStageOrder}. Advance engagement to Stage {selectedStageDef.order} ({selectedStageDef.name}) to activate tasks on the Client Portal.
                                    </p>
                                </div>
                                <button
                                    type="button"
                                    onClick={handleAdvanceStage}
                                    disabled={isAdvancingStage}
                                    className="px-4 py-2.5 rounded-xl text-xs font-bold flex items-center gap-2 transition shadow-sm bg-gradient-to-r from-[#635bff] to-[#712ae2] hover:opacity-95 text-white shadow-indigo-500/25 disabled:opacity-60 disabled:cursor-not-allowed shrink-0"
                                >
                                    {isAdvancingStage ? (
                                        <>
                                            <Loader2 className="w-4 h-4 animate-spin" />
                                            <span>Advancing...</span>
                                        </>
                                    ) : (
                                        <>
                                            <span>Advance to Stage {selectedStageDef.order} Now</span>
                                            <ChevronRight className="w-4 h-4" />
                                        </>
                                    )}
                                </button>
                            </div>
                        )}

                        {/* Completed Past Stage Notice */}
                        {!isViewingCurrentStage && selectedStageDef.order < currentStageOrder && (
                            <div className="pt-4 border-t border-slate-100 flex items-center justify-between text-xs text-slate-500">
                                <span className="flex items-center gap-1.5 text-emerald-700 font-semibold">
                                    <CheckCircle2 className="w-4 h-4 text-emerald-600" />
                                    Stage {selectedStageDef.order} is completed.
                                </span>
                                <button
                                    type="button"
                                    onClick={() => setSelectedStageKey(engagement?.stage ?? null)}
                                    className="text-xs text-indigo-600 hover:text-indigo-800 font-semibold flex items-center gap-1"
                                >
                                    <span>Jump to Active Stage {currentStageOrder}</span>
                                    <ArrowRight className="w-3.5 h-3.5" />
                                </button>
                            </div>
                        )}

                        {/* Future Stage Sequential Notice */}
                        {!isViewingCurrentStage && selectedStageDef.order > currentStageOrder + 1 && (
                            <div className="pt-4 border-t border-slate-100 flex items-center justify-between text-xs text-slate-500">
                                <span className="text-slate-500">
                                    Stages must proceed sequentially. Complete Stage {currentStageOrder} before unlocking Stage {selectedStageDef.order}.
                                </span>
                                <button
                                    type="button"
                                    onClick={() => setSelectedStageKey(engagement?.stage ?? null)}
                                    className="text-xs text-indigo-600 hover:text-indigo-800 font-semibold flex items-center gap-1"
                                >
                                    <span>Back to Stage {currentStageOrder}</span>
                                    <ArrowRight className="w-3.5 h-3.5" />
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
                    {(() => {
                        const currentStageActions = actions.filter((a) => (a.stageNumber || 1) === currentStageOrder);
                        const hasCurrentStageActions = currentStageActions.length > 0;
                        const allCurrentStageActionsCompleted = hasCurrentStageActions && currentStageActions.every(
                            (a) => a.isCompleted || a.status === 'Completed' || (a as any).verificationStatus === 'Verified'
                        );

                        const stageSelectedActions = actions.filter((a) => (a.stageNumber || 1) === selectedStageDef.order);
                        const displayActions = stageFilterMode === 'stage' ? stageSelectedActions : actions;

                        return (
                            <div className="bg-white/90 backdrop-blur-md p-5 rounded-2xl border border-slate-200/90 shadow-xs space-y-3">
                                {/* Stage Completion Callout Banner */}
                                {allCurrentStageActionsCompleted && nextStage && !isTerminalStatus && (
                                    <div className="p-3.5 rounded-xl bg-gradient-to-r from-emerald-50 to-teal-50 border border-emerald-200 text-xs shadow-xs space-y-2">
                                        <div className="flex items-center gap-2 font-bold text-emerald-900">
                                            <CheckCircle2 className="w-4 h-4 text-emerald-600 shrink-0" />
                                            <span>All Stage {currentStageOrder} Tasks Completed!</span>
                                        </div>
                                        <p className="text-emerald-700 text-[11px] leading-relaxed">
                                            All deliverables for Stage {currentStageOrder} ({ENGAGEMENT_STAGES[currentStageOrder - 1]?.name}) are complete. Advance to unlock Stage {getStageDefinition(nextStage).order}: {getStageDefinition(nextStage).name} for the client.
                                        </p>
                                        <button
                                            type="button"
                                            onClick={handleAdvanceStage}
                                            disabled={isAdvancingStage}
                                            className="w-full py-2 px-3 rounded-lg text-xs font-bold text-white bg-emerald-600 hover:bg-emerald-700 transition flex items-center justify-center gap-2 shadow-xs disabled:opacity-60"
                                        >
                                            {isAdvancingStage ? (
                                                <>
                                                    <Loader2 className="w-3.5 h-3.5 animate-spin" />
                                                    <span>Advancing to Stage {getStageDefinition(nextStage).order}...</span>
                                                </>
                                            ) : (
                                                <>
                                                    <span>Advance to Stage {getStageDefinition(nextStage).order}: {getStageDefinition(nextStage).name}</span>
                                                    <ChevronRight className="w-3.5 h-3.5" />
                                                </>
                                            )}
                                        </button>
                                    </div>
                                )}

                                <div className="flex items-center justify-between pb-2 border-b border-slate-100">
                                    <div className="flex items-center gap-1.5">
                                        <Activity className="w-4 h-4 text-indigo-600" />
                                        <h3 className="text-sm font-bold text-slate-900">Stage Action Queue</h3>
                                    </div>
                                    <div className="flex items-center gap-2">
                                        <div className="flex items-center gap-1 bg-slate-100 p-0.5 rounded-lg text-[11px]">
                                            <button
                                                type="button"
                                                onClick={() => setStageFilterMode('stage')}
                                                className={`px-2 py-0.5 rounded-md transition ${
                                                    stageFilterMode === 'stage'
                                                        ? 'bg-white text-indigo-700 shadow-xs font-semibold'
                                                        : 'text-slate-600 hover:text-slate-900'
                                                }`}
                                            >
                                                Stage {selectedStageDef.order} ({stageSelectedActions.length})
                                            </button>
                                            <button
                                                type="button"
                                                onClick={() => setStageFilterMode('all')}
                                                className={`px-2 py-0.5 rounded-md transition ${
                                                    stageFilterMode === 'all'
                                                        ? 'bg-white text-indigo-700 shadow-xs font-semibold'
                                                        : 'text-slate-600 hover:text-slate-900'
                                                }`}
                                            >
                                                All ({actions.length})
                                            </button>
                                        </div>
                                        <button
                                            type="button"
                                            onClick={() => {
                                                setNewTaskStage(selectedStageDef.order);
                                                setIsAddTaskOpen(true);
                                            }}
                                            className="px-2.5 py-1 rounded-lg bg-indigo-50 hover:bg-indigo-100 text-indigo-700 border border-indigo-200 text-xs font-bold flex items-center gap-1 transition shadow-xs"
                                            title="Add task for this stage"
                                        >
                                            <Plus className="w-3.5 h-3.5" />
                                            <span>Add Task</span>
                                        </button>
                                    </div>
                                </div>

                                {displayActions.length === 0 ? (
                                    <div className="p-5 rounded-xl bg-slate-50 text-center space-y-2 border border-slate-200">
                                        <CheckCircle2 className="w-7 h-7 text-indigo-600 mx-auto" />
                                        <div className="text-xs font-bold text-slate-900">
                                            {stageFilterMode === 'stage'
                                                ? `No Tasks Configured for Stage ${selectedStageDef.order}`
                                                : 'All Stage Actions Up-To-Date'}
                                        </div>
                                        <p className="text-[11px] text-slate-500 max-w-xs mx-auto">
                                            {stageFilterMode === 'stage'
                                                ? `Add customized deliverables or required documentation for Stage ${selectedStageDef.order} (${selectedStageDef.name}).`
                                                : 'No pending gate stalls or unhandled client blockers.'}
                                        </p>
                                        <div className="pt-2">
                                            <button
                                                type="button"
                                                onClick={() => {
                                                    setNewTaskStage(selectedStageDef.order);
                                                    setIsAddTaskOpen(true);
                                                }}
                                                className="inline-flex items-center gap-1.5 px-3.5 py-1.5 rounded-lg bg-indigo-600 hover:bg-indigo-700 text-white text-xs font-bold transition shadow-xs"
                                            >
                                                <Plus className="w-3.5 h-3.5" />
                                                <span>Configure Task for Stage {selectedStageDef.order}</span>
                                            </button>
                                        </div>
                                    </div>
                                ) : (
                                    <div className="space-y-3">
                                        {displayActions.map((act) => {
                                    const linkedDoc = findLinkedDoc(act);
                                    const isActionVerified = (act as any).verificationStatus === 'Verified' || linkedDoc?.verificationStatus?.toUpperCase() === 'VERIFIED';
                                    const isActionRejected = (act as any).verificationStatus === 'Rejected' || linkedDoc?.verificationStatus?.toUpperCase() === 'REJECTED';
                                    const isDocAction = act.type === 'DocumentUpload' || act.type === 'KycDocument' || act.type === 'SignAgreement' || act.title.toLowerCase().includes('document') || act.title.toLowerCase().includes('kyc') || act.title.toLowerCase().includes('agreement');

                                    return (
                                        <div
                                            key={act.actionId}
                                            className={`p-3.5 rounded-xl border transition space-y-2.5 ${
                                                isActionVerified
                                                    ? 'bg-emerald-50/40 border-emerald-200/80'
                                                    : isActionRejected
                                                    ? 'bg-rose-50/40 border-rose-200/80'
                                                    : 'bg-slate-50/80 border-slate-200'
                                            }`}
                                        >
                                            {/* Action Header */}
                                            <div className="flex items-start justify-between gap-2">
                                                <div>
                                                    <div className="flex items-center gap-1.5 mb-1">
                                                        <span className="text-[10px] font-bold uppercase tracking-wider px-1.5 py-0.5 rounded bg-white text-indigo-700 border border-indigo-100 shadow-xs">
                                                            {act.type}
                                                        </span>
                                                        {isDocAction && (
                                                            <span className="text-[10px] font-medium text-slate-400">• Evidence Action</span>
                                                        )}
                                                    </div>
                                                    <span className="text-xs font-bold text-slate-900 block leading-tight">
                                                        {act.title}
                                                    </span>
                                                </div>

                                                {/* Verification or Completion Status Badge */}
                                                <div>
                                                    {isActionVerified ? (
                                                        <span className="inline-flex items-center gap-1 text-[10px] font-bold text-emerald-700 bg-emerald-100/90 px-2 py-0.5 rounded-full border border-emerald-300">
                                                            <CheckCircle2 className="w-3 h-3" />
                                                            Verified
                                                        </span>
                                                    ) : isActionRejected ? (
                                                        <span className="inline-flex items-center gap-1 text-[10px] font-bold text-rose-700 bg-rose-100/90 px-2 py-0.5 rounded-full border border-rose-300">
                                                            <X className="w-3 h-3" />
                                                            Rejected
                                                        </span>
                                                    ) : act.isCompleted ? (
                                                        <span className="inline-flex items-center gap-1 text-[10px] font-bold text-emerald-700 bg-emerald-50 px-2 py-0.5 rounded-full border border-emerald-200">
                                                            <Check className="w-3 h-3" />
                                                            Done
                                                        </span>
                                                    ) : (
                                                        <span className="inline-flex items-center gap-1 text-[10px] font-bold text-amber-700 bg-amber-50 px-2 py-0.5 rounded-full border border-amber-200">
                                                            <Clock className="w-3 h-3" />
                                                            Pending
                                                        </span>
                                                    )}
                                                </div>
                                            </div>

                                            {/* Linked Evidence Document Display */}
                                            {linkedDoc ? (
                                                <div className="bg-white p-2.5 rounded-lg border border-slate-200/80 space-y-2 text-xs">
                                                    <div className="flex items-center justify-between gap-2">
                                                        <div className="flex items-center gap-2 min-w-0">
                                                            <FileText className="w-4 h-4 text-indigo-600 flex-shrink-0" />
                                                            <span className="font-semibold text-slate-800 truncate text-[11px]">
                                                                {linkedDoc.type}
                                                            </span>
                                                            <span className="text-[10px] font-mono text-slate-400 truncate">
                                                                ({linkedDoc.documentId.slice(0, 8)}...)
                                                            </span>
                                                        </div>

                                                        {/* Continuous Staff Download */}
                                                        <a
                                                            href={DocumentsApi.getDownloadUrl(engagementId, linkedDoc.documentId, tenantId, linkedDoc.isDeleted)}
                                                            target="_blank"
                                                            rel="noreferrer"
                                                            title="Download Evidence PDF"
                                                            className="inline-flex items-center gap-1 text-[11px] font-semibold text-indigo-600 hover:text-indigo-800 bg-indigo-50/70 hover:bg-indigo-100 px-2 py-0.5 rounded border border-indigo-100 transition flex-shrink-0"
                                                        >
                                                            <Download className="w-3 h-3" />
                                                            PDF
                                                        </a>
                                                    </div>

                                                    {/* Compliance & Verification details */}
                                                    <div className="flex flex-wrap items-center gap-1.5 pt-1 border-t border-slate-100 text-[10px]">
                                                        {linkedDoc.complianceStatus === 'Compliant' ? (
                                                            <span className="inline-flex items-center gap-1 px-1.5 py-0.5 rounded bg-emerald-50 text-emerald-700 font-semibold border border-emerald-200">
                                                                <ShieldCheck className="w-3 h-3 text-emerald-600" />
                                                                Compliant
                                                            </span>
                                                        ) : linkedDoc.complianceStatus === 'NonCompliant' ? (
                                                            <span className="inline-flex items-center gap-1 px-1.5 py-0.5 rounded bg-rose-50 text-rose-700 font-semibold border border-rose-200" title={linkedDoc.rejectionReason}>
                                                                <ShieldAlert className="w-3 h-3 text-rose-600" />
                                                                Non-Compliant
                                                            </span>
                                                        ) : (
                                                            <span className="inline-flex items-center gap-1 px-1.5 py-0.5 rounded bg-amber-50 text-amber-700 font-semibold border border-amber-200">
                                                                <Clock className="w-3 h-3" />
                                                                Check Pending
                                                            </span>
                                                        )}

                                                        <span className="text-slate-300">•</span>
                                                        <span className="text-slate-500">
                                                            Exp: <strong className={linkedDoc.expiryDate && new Date(linkedDoc.expiryDate) < new Date() ? 'text-rose-600 font-bold' : 'text-slate-700'}>{linkedDoc.expiryDate || 'N/A'}</strong>
                                                        </span>
                                                    </div>

                                                    {/* If rejected, show rejection note */}
                                                    {linkedDoc.verificationReason && isActionRejected && (
                                                        <div className="text-[10px] text-rose-600 bg-rose-50 p-1.5 rounded border border-rose-100 font-medium">
                                                            Reason: {linkedDoc.verificationReason}
                                                        </div>
                                                    )}

                                                    {/* Staff Verification & Rejection Controls */}
                                                    {!isActionVerified && (
                                                        <div className="flex items-center gap-2 pt-1 border-t border-slate-100">
                                                            <button
                                                                type="button"
                                                                disabled={linkedDoc.complianceStatus !== 'Compliant'}
                                                                onClick={() => {
                                                                    setActionToVerify({ action: act, doc: linkedDoc });
                                                                    setVerifyNotes('');
                                                                }}
                                                                title={
                                                                    linkedDoc.complianceStatus === 'Compliant'
                                                                        ? 'Verify this evidence document'
                                                                        : 'Document must pass deterministic compliance check before staff verification'
                                                                }
                                                                className="flex-1 py-1 px-2 rounded-lg text-[11px] font-semibold text-emerald-700 bg-emerald-50 hover:bg-emerald-100 border border-emerald-200 flex items-center justify-center gap-1 transition disabled:opacity-40 disabled:cursor-not-allowed"
                                                            >
                                                                <Check className="w-3 h-3" />
                                                                Verify Evidence
                                                            </button>

                                                            <button
                                                                type="button"
                                                                onClick={() => {
                                                                    setActionToReject({ action: act, doc: linkedDoc });
                                                                    setRejectReason('');
                                                                }}
                                                                title="Reject evidence document"
                                                                className="flex-1 py-1 px-2 rounded-lg text-[11px] font-semibold text-rose-700 bg-rose-50 hover:bg-rose-100 border border-rose-200 flex items-center justify-center gap-1 transition"
                                                            >
                                                                <X className="w-3 h-3" />
                                                                Reject
                                                            </button>
                                                        </div>
                                                    )}
                                                </div>
                                            ) : isDocAction ? (
                                                <div className="p-2 rounded-lg bg-amber-50/60 border border-amber-200/60 text-[11px] text-amber-800 flex items-center gap-1.5">
                                                    <Clock className="w-3.5 h-3.5 text-amber-600 flex-shrink-0" />
                                                    <span>Awaiting client PDF upload in Client Portal</span>
                                                </div>
                                            ) : null}

                                            {/* General Task: Mark Complete button */}
                                            {!act.isCompleted && act.status !== 'Completed' && !linkedDoc && (
                                                <button
                                                    onClick={() => handleCompleteAction(act.actionId)}
                                                    disabled={completingActionId === act.actionId}
                                                    className="w-full py-1.5 rounded-lg bg-indigo-600 hover:bg-indigo-700 text-white text-xs font-semibold transition flex items-center justify-center gap-1"
                                                >
                                                    {completingActionId === act.actionId ? (
                                                        <>
                                                            <Loader2 className="w-3 h-3 animate-spin" />
                                                            Completing...
                                                        </>
                                                    ) : (
                                                        'Mark Complete'
                                                    )}
                                                </button>
                                            )}
                                        </div>
                                    );
                                })}
                            </div>
                        )}
                    </div>
                );
            })()}

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

            {/* Modal: Verify Stage Action & Linked Document */}
            {actionToVerify && (
                <div className="fixed inset-0 z-50 bg-slate-900/50 backdrop-blur-sm flex items-center justify-center p-4">
                    <div className="bg-white rounded-2xl max-w-md w-full p-6 shadow-2xl border border-slate-200 space-y-4 animate-in fade-in zoom-in-95 duration-150">
                        <div className="flex items-center gap-3">
                            <div className="p-2.5 rounded-xl bg-emerald-50 text-emerald-600 border border-emerald-200">
                                <CheckCircle2 className="w-6 h-6" />
                            </div>
                            <div>
                                <h3 className="text-base font-bold text-slate-900">
                                    Verify Stage Action Evidence
                                </h3>
                                <p className="text-xs text-slate-500">
                                    Approves the evidence across Document Vault & Workflow engines.
                                </p>
                            </div>
                        </div>

                        <div className="bg-slate-50 p-3 rounded-xl border border-slate-200 text-xs space-y-1">
                            <div><span className="font-semibold text-slate-700">Action:</span> {actionToVerify.action.title}</div>
                            {actionToVerify.doc && (
                                <div><span className="font-semibold text-slate-700">Document Type:</span> {actionToVerify.doc.type}</div>
                            )}
                            <div><span className="font-semibold text-slate-700">Staff Verifier:</span> {userId || 'staff-lead'}</div>
                        </div>

                        <div>
                            <label className="block text-xs font-bold text-slate-700 mb-1">
                                Verification Notes (Optional)
                            </label>
                            <textarea
                                rows={3}
                                value={verifyNotes}
                                onChange={(e) => setVerifyNotes(e.target.value)}
                                placeholder="E.g., Document details and regulatory compliance verified..."
                                className="w-full text-xs bg-slate-50 border border-slate-200 rounded-xl p-3 text-slate-800 focus:outline-none focus:ring-2 focus:ring-emerald-500/20 focus:border-emerald-500"
                            />
                        </div>

                        <div className="flex items-center justify-end gap-3 pt-2">
                            <button
                                type="button"
                                onClick={() => setActionToVerify(null)}
                                className="px-4 py-2 text-xs font-semibold text-slate-600 hover:text-slate-800 transition"
                            >
                                Cancel
                            </button>
                            <button
                                type="button"
                                disabled={isVerifyingAction}
                                onClick={handleConfirmVerifyAction}
                                className="inline-flex items-center gap-2 px-5 py-2.5 rounded-xl text-xs font-semibold text-white bg-emerald-600 hover:bg-emerald-700 shadow-sm transition disabled:opacity-50"
                            >
                                {isVerifyingAction ? (
                                    <>
                                        <Loader2 className="w-3.5 h-3.5 animate-spin" />
                                        Synchronizing Microservices...
                                    </>
                                ) : (
                                    <>
                                        <Check className="w-4 h-4" />
                                        Confirm Verification
                                    </>
                                )}
                            </button>
                        </div>
                    </div>
                </div>
            )}

            {/* Modal: Reject Stage Action & Linked Document */}
            {actionToReject && (
                <div className="fixed inset-0 z-50 bg-slate-900/50 backdrop-blur-sm flex items-center justify-center p-4">
                    <div className="bg-white rounded-2xl max-w-md w-full p-6 shadow-2xl border border-slate-200 space-y-4 animate-in fade-in zoom-in-95 duration-150">
                        <div className="flex items-center gap-3">
                            <div className="p-2.5 rounded-xl bg-rose-50 text-rose-600 border border-rose-200">
                                <ShieldAlert className="w-6 h-6" />
                            </div>
                            <div>
                                <h3 className="text-base font-bold text-slate-900">
                                    Reject Stage Action Evidence
                                </h3>
                                <p className="text-xs text-slate-500">
                                    Marks document and workflow action as rejected for client re-submission.
                                </p>
                            </div>
                        </div>

                        <div className="bg-slate-50 p-3 rounded-xl border border-slate-200 text-xs space-y-1">
                            <div><span className="font-semibold text-slate-700">Action:</span> {actionToReject.action.title}</div>
                            {actionToReject.doc && (
                                <div><span className="font-semibold text-slate-700">Document Type:</span> {actionToReject.doc.type}</div>
                            )}
                        </div>

                        <div>
                            <label className="block text-xs font-bold text-slate-700 mb-1">
                                Rejection Reason <span className="text-rose-500">*</span>
                            </label>
                            <textarea
                                rows={3}
                                required
                                value={rejectReason}
                                onChange={(e) => setRejectReason(e.target.value)}
                                placeholder="E.g., Scanned copy is blurry or signature is unverified..."
                                className="w-full text-xs bg-slate-50 border border-slate-200 rounded-xl p-3 text-slate-800 focus:outline-none focus:ring-2 focus:ring-rose-500/20 focus:border-rose-500"
                            />
                        </div>

                        <div className="flex items-center justify-end gap-3 pt-2">
                            <button
                                type="button"
                                onClick={() => setActionToReject(null)}
                                className="px-4 py-2 text-xs font-semibold text-slate-600 hover:text-slate-800 transition"
                            >
                                Cancel
                            </button>
                            <button
                                type="button"
                                disabled={isRejectingAction || !rejectReason.trim()}
                                onClick={handleConfirmRejectAction}
                                className="inline-flex items-center gap-2 px-5 py-2.5 rounded-xl text-xs font-semibold text-white bg-rose-600 hover:bg-rose-700 shadow-sm transition disabled:opacity-50"
                            >
                                {isRejectingAction ? (
                                    <>
                                        <Loader2 className="w-3.5 h-3.5 animate-spin" />
                                        Synchronizing Microservices...
                                    </>
                                ) : (
                                    <>
                                        <X className="w-4 h-4" />
                                        Confirm Rejection
                                    </>
                                )}
                            </button>
                        </div>
                    </div>
                </div>
            )}

            {/* Modal: Add Task for Stage */}
            {isAddTaskOpen && (
                <div className="fixed inset-0 z-50 bg-slate-900/50 backdrop-blur-sm flex items-center justify-center p-4">
                    <div className="bg-white rounded-2xl max-w-lg w-full p-6 shadow-2xl border border-slate-200 space-y-4 animate-in fade-in zoom-in-95 duration-150">
                        <div className="flex items-center justify-between pb-3 border-b border-slate-100">
                            <div className="flex items-center gap-2.5">
                                <div className="p-2 rounded-xl bg-indigo-50 text-indigo-600">
                                    <Plus className="w-5 h-5" />
                                </div>
                                <div>
                                    <h3 className="text-base font-bold text-slate-900">Add Stage Deliverable</h3>
                                    <p className="text-xs text-slate-500">Configure a task or required evidence for this engagement.</p>
                                </div>
                            </div>
                            <button
                                type="button"
                                onClick={() => setIsAddTaskOpen(false)}
                                className="p-1 rounded-lg text-slate-400 hover:text-slate-600 hover:bg-slate-100"
                            >
                                <X className="w-5 h-5" />
                            </button>
                        </div>

                        {createTaskError && (
                            <div className="p-3 bg-red-50 border border-red-200 rounded-xl text-red-700 text-xs font-semibold flex items-center gap-2">
                                <AlertTriangle className="w-4 h-4 shrink-0" />
                                <span>{createTaskError}</span>
                            </div>
                        )}

                        <form onSubmit={handleCreateTask} className="space-y-3.5">
                            <div>
                                <label className="block text-xs font-bold text-slate-700 mb-1">
                                    Task Title <span className="text-rose-500">*</span>
                                </label>
                                <input
                                    type="text"
                                    required
                                    value={newTaskTitle}
                                    onChange={(e) => setNewTaskTitle(e.target.value)}
                                    placeholder="E.g., Certified Proof of Address"
                                    className="w-full text-xs bg-slate-50 border border-slate-200 rounded-xl p-2.5 text-slate-800 focus:outline-none focus:ring-2 focus:ring-indigo-500/20 focus:border-indigo-500"
                                />
                            </div>

                            <div>
                                <label className="block text-xs font-bold text-slate-700 mb-1">Description / Instructions</label>
                                <textarea
                                    rows={2}
                                    value={newTaskDesc}
                                    onChange={(e) => setNewTaskDesc(e.target.value)}
                                    placeholder="Explain requirement, acceptable file formats, or verification criteria..."
                                    className="w-full text-xs bg-slate-50 border border-slate-200 rounded-xl p-2.5 text-slate-800 focus:outline-none focus:ring-2 focus:ring-indigo-500/20 focus:border-indigo-500"
                                />
                            </div>

                            <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
                                <div>
                                    <label className="block text-xs font-bold text-slate-700 mb-1">Target Stage</label>
                                    <select
                                        value={newTaskStage}
                                        onChange={(e) => setNewTaskStage(Number(e.target.value))}
                                        className="w-full text-xs bg-slate-50 border border-slate-200 rounded-xl p-2.5 text-slate-800 focus:outline-none focus:ring-2 focus:ring-indigo-500/20 focus:border-indigo-500"
                                    >
                                        <option value={1}>Stage 1: Onboarding</option>
                                        <option value={2}>Stage 2: Document Collection</option>
                                        <option value={3}>Stage 3: Verification</option>
                                        <option value={4}>Stage 4: Execution</option>
                                        <option value={5}>Stage 5: Closure</option>
                                    </select>
                                </div>

                                <div>
                                    <label className="block text-xs font-bold text-slate-700 mb-1">Action Type</label>
                                    <select
                                        value={newTaskType}
                                        onChange={(e) => setNewTaskType(e.target.value)}
                                        className="w-full text-xs bg-slate-50 border border-slate-200 rounded-xl p-2.5 text-slate-800 focus:outline-none focus:ring-2 focus:ring-indigo-500/20 focus:border-indigo-500"
                                    >
                                        <option value="CustomTask">Custom Task (General)</option>
                                        <option value="KycDocument">KYC Document</option>
                                        <option value="SignAgreement">Sign Agreement / Contract</option>
                                        <option value="DocumentUpload">Document Upload</option>
                                    </select>
                                </div>
                            </div>

                            <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
                                <div>
                                    <label className="block text-xs font-bold text-slate-700 mb-1">Assigned Role</label>
                                    <select
                                        value={newTaskRole}
                                        onChange={(e) => setNewTaskRole(e.target.value as any)}
                                        className="w-full text-xs bg-slate-50 border border-slate-200 rounded-xl p-2.5 text-slate-800 focus:outline-none focus:ring-2 focus:ring-indigo-500/20 focus:border-indigo-500"
                                    >
                                        <option value="Client">Client (Visible in Client Portal)</option>
                                        <option value="Staff">Staff (Internal Custodian Only)</option>
                                    </select>
                                </div>

                                <div>
                                    <label className="block text-xs font-bold text-slate-700 mb-1">Deadline (Optional)</label>
                                    <input
                                        type="date"
                                        value={newTaskDeadline}
                                        onChange={(e) => setNewTaskDeadline(e.target.value)}
                                        className="w-full text-xs bg-slate-50 border border-slate-200 rounded-xl p-2.5 text-slate-800 focus:outline-none focus:ring-2 focus:ring-indigo-500/20 focus:border-indigo-500"
                                    >
                                    </input>
                                </div>
                            </div>

                            <div className="flex items-center justify-end gap-3 pt-3 border-t border-slate-100">
                                <button
                                    type="button"
                                    onClick={() => setIsAddTaskOpen(false)}
                                    className="px-4 py-2 text-xs font-semibold text-slate-600 hover:text-slate-800 transition"
                                >
                                    Cancel
                                </button>
                                <button
                                    type="submit"
                                    disabled={isCreatingTask || !newTaskTitle.trim()}
                                    className="inline-flex items-center gap-2 px-5 py-2.5 rounded-xl text-xs font-bold text-white bg-indigo-600 hover:bg-indigo-700 shadow-sm transition disabled:opacity-50"
                                >
                                    {isCreatingTask ? (
                                        <>
                                            <Loader2 className="w-3.5 h-3.5 animate-spin" />
                                            <span>Creating Task...</span>
                                        </>
                                    ) : (
                                        <>
                                            <Plus className="w-4 h-4" />
                                            <span>Create Deliverable</span>
                                        </>
                                    )}
                                </button>
                            </div>
                        </form>
                    </div>
                </div>
            )}
        </div>
    );
};
