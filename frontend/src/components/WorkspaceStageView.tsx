import React, { useState, useEffect, useCallback } from 'react';
import { useNavigate } from 'react-router-dom';
import { useAuth } from '../context/AuthContext';
import { WorkflowApi, IdentityApi, DocumentsApi } from '../services/api';
import { Engagement, ClientProfile, UserAccountResponse, ClientAction, DocumentMetadata, EngagementStage, EngagementCondition, ConditionType, ConditionPaymentType, AttachConditionRequest, UpdateConditionRequest } from '../types';
import { ENGAGEMENT_STAGES, getStageDefinition, getStageIndex, getNextStage, computeClientVisibleStageNumber } from '../constants/engagementStages';
import { ACTION_TYPE_TO_DOCUMENT_TYPE } from '../constants/documentTypes';
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
    Plus,
    CreditCard,
    CheckSquare,
    DollarSign,
    AlertCircle,
    Edit3,
    Trash2,
    Info,
    Ban,
    XCircle
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
    const [conditions, setConditions] = useState<EngagementCondition[]>([]);
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

    // CSTD-24: Conditions Modals State
    const [isAddConditionOpen, setIsAddConditionOpen] = useState(false);
    const [newCondType, setNewCondType] = useState<ConditionType>('Approval');
    const [newCondTitle, setNewCondTitle] = useState('');
    const [newCondDesc, setNewCondDesc] = useState('');
    const [newCondStage, setNewCondStage] = useState<EngagementStage>('Verification');
    const [newCondDueDate, setNewCondDueDate] = useState('');
    const [newCondAmount, setNewCondAmount] = useState<number | ''>('');
    const [newCondCurrency, setNewCondCurrency] = useState('USD');
    const [newCondPaymentType, setNewCondPaymentType] = useState<ConditionPaymentType>('Milestone');
    const [newCondInternalNote, setNewCondInternalNote] = useState('');
    const [isCreatingCondition, setIsCreatingCondition] = useState(false);
    const [createConditionError, setCreateConditionError] = useState<string | null>(null);

    const [conditionToEdit, setConditionToEdit] = useState<EngagementCondition | null>(null);
    const [editCondTitle, setEditCondTitle] = useState('');
    const [editCondDesc, setEditCondDesc] = useState('');
    const [editCondDueDate, setEditCondDueDate] = useState('');
    const [editCondAmount, setEditCondAmount] = useState<number | ''>('');
    const [editCondCurrency, setEditCondCurrency] = useState('USD');
    const [editCondPaymentType, setEditCondPaymentType] = useState<ConditionPaymentType>('Milestone');
    const [editCondInternalNote, setEditCondInternalNote] = useState('');
    const [isUpdatingCondition, setIsUpdatingCondition] = useState(false);
    const [updateConditionError, setUpdateConditionError] = useState<string | null>(null);

    const [conditionToDeactivate, setConditionToDeactivate] = useState<EngagementCondition | null>(null);
    const [deactivateReason, setDeactivateReason] = useState('');
    const [isDeactivatingCondition, setIsDeactivatingCondition] = useState(false);
    const [deactivateConditionError, setDeactivateConditionError] = useState<string | null>(null);

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

        // Same mapping UploadEvidenceModal uploads under — kept in one shared place so this
        // lookup can never drift from what's actually uploaded (see constants/documentTypes.ts).
        const mappedDocType = ACTION_TYPE_TO_DOCUMENT_TYPE[act.type as string];
        if (mappedDocType) {
            const found = documents.find((d) => d.type === mappedDocType);
            if (found) return found;
        }

        // Fallback for staff-created custom tasks with a descriptive title but a generic Type.
        if (act.title.toLowerCase().includes('kyc') || act.title.toLowerCase().includes('passport')) {
            const found = documents.find((d) => d.type === 'KYC_PASSPORT');
            if (found) return found;
        }

        if (act.title.toLowerCase().includes('agreement') || act.title.toLowerCase().includes('contract')) {
            const found = documents.find((d) => d.type === 'SIGNED_AGREEMENT');
            if (found) return found;
        }

        if (act.title.toLowerCase().includes('proof of address') || act.title.toLowerCase().includes('address')) {
            const found = documents.find((d) => d.type === 'PROOF_OF_ADDRESS');
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
                const actList = await WorkflowApi.getActions(engagementId, tenantId, false);
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

            try {
                const condList = await WorkflowApi.getConditions(engagementId, tenantId, true);
                setConditions(condList || []);
            } catch (cErr) {
                console.warn('Failed to load engagement conditions:', cErr);
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
                    assignedToRole: newTaskRole as any,
                    deadlineUtc: newTaskDeadline ? new Date(newTaskDeadline).toISOString() : undefined,
                    isInternalOnly: newTaskRole === 'Staff',
                    // Backend's CreateClientActionDto.Source is [Required] — omitting it
                    // previously failed ModelState validation (400) before the task was ever
                    // persisted. Mirrors the seeded rows' Source: "LifecycleDefault" convention.
                    source: 'StaffManual',
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

    // CSTD-24: Condition Management Handlers
    const handleOpenAddCondition = () => {
        setCreateConditionError(null);
        setNewCondType('Approval');
        setNewCondTitle('');
        setNewCondDesc('');
        setNewCondDueDate('');
        setNewCondAmount('');
        setNewCondCurrency('USD');
        setNewCondPaymentType('Milestone');
        setNewCondInternalNote('');

        const eligibleStages = ENGAGEMENT_STAGES.filter((s) => s.order > currentStageOrder);
        if (eligibleStages.length > 0) {
            setNewCondStage(eligibleStages[0].key);
        }
        setIsAddConditionOpen(true);
    };

    const handleCreateCondition = async (e: React.FormEvent) => {
        e.preventDefault();
        if (!tenantId || !engagementId) return;

        if (!newCondTitle.trim()) {
            setCreateConditionError('Title is required.');
            return;
        }

        if (newCondType === 'Payment') {
            const numAmount = typeof newCondAmount === 'number' ? newCondAmount : parseFloat(String(newCondAmount));
            if (!numAmount || numAmount <= 0) {
                setCreateConditionError('Payment condition requires an amount greater than 0.');
                return;
            }
            if (!newCondCurrency || newCondCurrency.trim().length !== 3) {
                setCreateConditionError('Currency must be a 3-letter ISO code (e.g. USD, EUR, GBP).');
                return;
            }
        }

        setIsCreatingCondition(true);
        setCreateConditionError(null);

        try {
            const req: AttachConditionRequest = {
                type: newCondType,
                requiredBeforeStage: newCondStage,
                title: newCondTitle.trim(),
                description: newCondDesc.trim() || undefined,
                dueDateUtc: newCondDueDate ? new Date(newCondDueDate).toISOString() : undefined,
                internalNote: newCondInternalNote.trim() || undefined,
            };

            if (newCondType === 'Payment') {
                req.amount = typeof newCondAmount === 'number' ? newCondAmount : parseFloat(String(newCondAmount));
                req.currency = newCondCurrency.trim().toUpperCase();
                req.paymentType = newCondPaymentType;
            }

            await WorkflowApi.attachCondition(engagementId, req, tenantId);
            await loadWorkspaceData();
            setIsAddConditionOpen(false);
        } catch (err: any) {
            console.error('Failed to attach condition:', err);
            setCreateConditionError(err.message || 'Failed to attach condition.');
        } finally {
            setIsCreatingCondition(false);
        }
    };

    const handleOpenEditCondition = (cond: EngagementCondition) => {
        setUpdateConditionError(null);
        setConditionToEdit(cond);
        setEditCondTitle(cond.title);
        setEditCondDesc(cond.description || '');
        setEditCondDueDate(cond.dueDateUtc ? cond.dueDateUtc.slice(0, 10) : '');
        setEditCondAmount(cond.amount ?? '');
        setEditCondCurrency(cond.currency || 'USD');
        setEditCondPaymentType((cond.paymentType as ConditionPaymentType) || 'Milestone');
        setEditCondInternalNote(cond.internalNote || '');
    };

    const handleUpdateCondition = async (e: React.FormEvent) => {
        e.preventDefault();
        if (!tenantId || !engagementId || !conditionToEdit) return;

        if (!editCondTitle.trim()) {
            setUpdateConditionError('Title is required.');
            return;
        }

        if (conditionToEdit.type === 'Payment') {
            const numAmount = typeof editCondAmount === 'number' ? editCondAmount : parseFloat(String(editCondAmount));
            if (!numAmount || numAmount <= 0) {
                setUpdateConditionError('Payment condition requires an amount greater than 0.');
                return;
            }
            if (!editCondCurrency || editCondCurrency.trim().length !== 3) {
                setUpdateConditionError('Currency must be a 3-letter ISO code (e.g. USD).');
                return;
            }
        }

        setIsUpdatingCondition(true);
        setUpdateConditionError(null);

        try {
            const req: UpdateConditionRequest = {
                title: editCondTitle.trim(),
                description: editCondDesc.trim() || undefined,
                dueDateUtc: editCondDueDate ? new Date(editCondDueDate).toISOString() : undefined,
                internalNote: editCondInternalNote.trim() || undefined,
            };

            if (conditionToEdit.type === 'Payment') {
                req.amount = typeof editCondAmount === 'number' ? editCondAmount : parseFloat(String(editCondAmount));
                req.currency = editCondCurrency.trim().toUpperCase();
                req.paymentType = editCondPaymentType;
            }

            await WorkflowApi.updateCondition(engagementId, conditionToEdit.conditionId, req, tenantId);
            await loadWorkspaceData();
            setConditionToEdit(null);
        } catch (err: any) {
            console.error('Failed to update condition:', err);
            setUpdateConditionError(err.message || 'Failed to update condition.');
        } finally {
            setIsUpdatingCondition(false);
        }
    };

    const handleOpenDeactivateCondition = (cond: EngagementCondition) => {
        setDeactivateConditionError(null);
        setDeactivateReason('');
        setConditionToDeactivate(cond);
    };

    const handleConfirmDeactivateCondition = async (e: React.FormEvent) => {
        e.preventDefault();
        if (!tenantId || !engagementId || !conditionToDeactivate) return;

        if (!deactivateReason.trim()) {
            setDeactivateConditionError('Deactivation reason is required.');
            return;
        }

        setIsDeactivatingCondition(true);
        setDeactivateConditionError(null);

        try {
            await WorkflowApi.deactivateCondition(engagementId, conditionToDeactivate.conditionId, deactivateReason.trim(), tenantId);
            await loadWorkspaceData();
            setConditionToDeactivate(null);
        } catch (err: any) {
            console.error('Failed to deactivate condition:', err);
            setDeactivateConditionError(err.message || 'Failed to deactivate condition.');
        } finally {
            setIsDeactivatingCondition(false);
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

    // The REAL, gate-checked stage — what PUT /stage actually operates on. Drives the
    // Advance Stage action and stage-detail panel below; must never be swapped for the
    // client-visible number, since the backend validates transitions against this value.
    const currentStageOrder = getStageIndex(engagement?.stage) + 1; // 0 if unknown, else 1-5
    // The stage number the Client Portal currently shows this client (CSTD-17/18 alignment
    // fix) — can run ahead of currentStageOrder when a client's tasks are all completed via
    // staff review/verification without the official engagement.stage having been advanced
    // yet. Used only for the pipeline stepper's "current/passed" display, so staff always see
    // the same headline stage number the client sees, distinct from the actionable stage below.
    const clientVisibleStageOrder = computeClientVisibleStageNumber(engagement, actions);
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
                    <span
                        className="px-3 py-1 rounded-full text-xs font-semibold bg-indigo-50 text-indigo-700 border border-indigo-200"
                        title="Matches the stage shown in this client's Client Portal view"
                    >
                        Client Portal shows: Stage {clientVisibleStageOrder} of 5
                    </span>
                </div>

                {/* 5 Step Cards — "passed"/"current" reflect clientVisibleStageOrder so this
                    stepper always agrees with what the client sees in their portal; the
                    "selected" (previewed) card and its detail panel below stay driven by the
                    real, gate-checked engagement.stage regardless of which card is highlighted
                    here as current, since that's what the Advance Stage action operates on. */}
                <div className="grid grid-cols-1 md:grid-cols-5 gap-3">
                    {ENGAGEMENT_STAGES.map((st) => {
                        const isPassed = st.order < clientVisibleStageOrder;
                        const isCurrent = st.order === clientVisibleStageOrder;
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

                    {/* CSTD-24: Engagement Conditions Management Panel */}
                    <div className="bg-white/90 backdrop-blur-md p-6 rounded-2xl border border-slate-200/90 shadow-xs space-y-4">
                        <div className="flex flex-col sm:flex-row sm:items-center justify-between gap-3 pb-3 border-b border-slate-100">
                            <div className="flex items-center gap-3">
                                <div className="p-2.5 rounded-xl bg-violet-50 text-violet-700 border border-violet-100">
                                    <ShieldCheck className="w-5 h-5" />
                                </div>
                                <div>
                                    <div className="flex items-center gap-2">
                                        <h3 className="text-base font-bold text-slate-900">Engagement Conditions</h3>
                                        <span className="px-2 py-0.5 rounded-full text-[10px] font-bold bg-violet-100 text-violet-700">
                                            {conditions.filter((c) => c.isActive).length} Active Gate{conditions.filter((c) => c.isActive).length === 1 ? '' : 's'}
                                        </span>
                                    </div>
                                    <p className="text-xs text-slate-500">
                                        Approval and Payment gating conditions that block stage advancement until satisfied.
                                    </p>
                                </div>
                            </div>

                            <button
                                type="button"
                                onClick={handleOpenAddCondition}
                                disabled={isTerminalStatus || currentStageOrder >= 5}
                                className="inline-flex items-center gap-1.5 px-3.5 py-2 rounded-xl text-xs font-bold text-white bg-gradient-to-r from-[#635bff] to-[#712ae2] hover:opacity-95 shadow-sm transition disabled:opacity-50 disabled:cursor-not-allowed shrink-0"
                            >
                                <Plus className="w-4 h-4" />
                                <span>Attach Condition</span>
                            </button>
                        </div>

                        {/* Condition List or Empty State */}
                        {conditions.length === 0 ? (
                            <div className="text-center py-8 px-4 rounded-xl border border-dashed border-slate-200 bg-slate-50/50 space-y-2">
                                <ShieldCheck className="w-8 h-8 text-slate-400 mx-auto" />
                                <p className="text-xs font-bold text-slate-700">No Conditions Attached</p>
                                <p className="text-[11px] text-slate-500 max-w-sm mx-auto">
                                    Attach an Approval or Payment condition to gate stage advancement. Conditions enforce client compliance before unlocking subsequent pipeline stages.
                                </p>
                            </div>
                        ) : (
                            <div className="space-y-3">
                                {conditions.map((cond) => {
                                    const isPayment = cond.type === 'Payment';
                                    const stageDef = getStageDefinition(cond.requiredBeforeStage as EngagementStage);
                                    const isEditable = cond.isActive && cond.status === 'Pending';

                                    return (
                                        <div
                                            key={cond.conditionId}
                                            className={`p-4 rounded-xl border transition ${
                                                !cond.isActive
                                                    ? 'bg-slate-50/60 border-slate-200 opacity-70'
                                                    : cond.status === 'Satisfied'
                                                    ? 'bg-emerald-50/40 border-emerald-200'
                                                    : cond.status === 'Rejected'
                                                    ? 'bg-rose-50/40 border-rose-200'
                                                    : 'bg-white border-slate-200/90 shadow-xs'
                                            }`}
                                        >
                                            <div className="flex flex-col sm:flex-row sm:items-start justify-between gap-3">
                                                <div className="space-y-1.5 flex-1">
                                                    <div className="flex flex-wrap items-center gap-2">
                                                        {/* Type Badge */}
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

                                                        {/* Status Badge */}
                                                        <span
                                                            className={`inline-flex items-center gap-1 px-2.5 py-0.5 rounded-full text-[10px] font-bold ${
                                                                cond.status === 'Satisfied'
                                                                    ? 'bg-emerald-100 text-emerald-800'
                                                                    : cond.status === 'Rejected'
                                                                    ? 'bg-rose-100 text-rose-800'
                                                                    : 'bg-amber-100 text-amber-800'
                                                            }`}
                                                        >
                                                            {cond.status === 'Satisfied' ? (
                                                                <CheckCircle2 className="w-3 h-3" />
                                                            ) : cond.status === 'Rejected' ? (
                                                                <XCircle className="w-3 h-3" />
                                                            ) : (
                                                                <Clock className="w-3 h-3" />
                                                            )}
                                                            {cond.status}
                                                        </span>

                                                        {/* Active/Inactive Badge */}
                                                        {!cond.isActive && (
                                                            <span className="inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-[10px] font-bold bg-slate-200 text-slate-700">
                                                                <Ban className="w-3 h-3" />
                                                                Deactivated
                                                            </span>
                                                        )}

                                                        {/* Gated Stage */}
                                                        <span className="text-[10px] font-semibold px-2 py-0.5 rounded bg-slate-100 text-slate-700 border border-slate-200">
                                                            Gates: Stage {stageDef.order} ({stageDef.name})
                                                        </span>

                                                        {/* Overdue Tag */}
                                                        {cond.isOverdue && cond.isActive && cond.status === 'Pending' && (
                                                            <span className="text-[10px] font-bold px-2 py-0.5 rounded-full bg-rose-100 text-rose-700 border border-rose-200 animate-pulse">
                                                                Overdue
                                                            </span>
                                                        )}
                                                    </div>

                                                    <h4 className="text-sm font-bold text-slate-900">{cond.title}</h4>
                                                    {cond.description && (
                                                        <p className="text-xs text-slate-600 leading-relaxed">{cond.description}</p>
                                                    )}

                                                    {/* Payment Details */}
                                                    {isPayment && (
                                                        <div className="flex flex-wrap items-center gap-3 pt-1 text-xs">
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

                                                    {/* Due Date */}
                                                    {cond.dueDateUtc && (
                                                        <div className="flex items-center gap-1.5 text-[11px] text-slate-500 pt-0.5">
                                                            <Calendar className="w-3.5 h-3.5 text-slate-400" />
                                                            <span>Due: {new Date(cond.dueDateUtc).toLocaleDateString()}</span>
                                                        </div>
                                                    )}

                                                    {/* Staff Internal Note (Internal Staff Only) */}
                                                    {cond.internalNote && (
                                                        <div className="p-2 rounded-lg bg-amber-50/70 border border-amber-200/80 text-[11px] text-amber-900 flex items-start gap-1.5 mt-1">
                                                            <Lock className="w-3 h-3 text-amber-600 shrink-0 mt-0.5" />
                                                            <div>
                                                                <strong className="font-semibold text-amber-950">Internal Staff Note: </strong>
                                                                {cond.internalNote}
                                                            </div>
                                                        </div>
                                                    )}

                                                    {/* Deactivation Audit Details */}
                                                    {!cond.isActive && cond.deactivationReason && (
                                                        <div className="p-2 rounded-lg bg-slate-100 border border-slate-200 text-[11px] text-slate-600 flex items-start gap-1.5 mt-1">
                                                            <Info className="w-3 h-3 text-slate-400 shrink-0 mt-0.5" />
                                                            <div>
                                                                <strong className="font-semibold text-slate-700">Deactivation Reason: </strong>
                                                                {cond.deactivationReason}
                                                                {cond.deactivatedBy && (
                                                                    <span className="text-slate-400"> (by {cond.deactivatedBy})</span>
                                                                )}
                                                            </div>
                                                        </div>
                                                    )}
                                                </div>

                                                {/* Action Buttons */}
                                                {isEditable && (
                                                    <div className="flex items-center gap-1.5 shrink-0 sm:self-center">
                                                        <button
                                                            type="button"
                                                            onClick={() => handleOpenEditCondition(cond)}
                                                            className="p-1.5 rounded-lg text-slate-500 hover:text-indigo-600 hover:bg-indigo-50 border border-slate-200 transition"
                                                            title="Edit condition"
                                                        >
                                                            <Edit3 className="w-3.5 h-3.5" />
                                                        </button>
                                                        <button
                                                            type="button"
                                                            onClick={() => handleOpenDeactivateCondition(cond)}
                                                            className="p-1.5 rounded-lg text-slate-500 hover:text-rose-600 hover:bg-rose-50 border border-slate-200 transition"
                                                            title="Deactivate condition"
                                                        >
                                                            <Trash2 className="w-3.5 h-3.5" />
                                                        </button>
                                                    </div>
                                                )}
                                            </div>
                                        </div>
                                    );
                                })}
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
                                    const isActionCancelled = act.status === 'Cancelled';
                                    const isDocAction = act.type === 'DocumentUpload' || act.type === 'KycDocument' || act.type === 'SignAgreement' || act.title.toLowerCase().includes('document') || act.title.toLowerCase().includes('kyc') || act.title.toLowerCase().includes('agreement');

                                    return (
                                        <div
                                            key={act.actionId}
                                            className={`p-3.5 rounded-xl border transition space-y-2.5 ${
                                                isActionCancelled
                                                    ? 'bg-slate-100/60 border-slate-200 opacity-60'
                                                    : isActionVerified
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
                                                        <span className="text-[10px] font-semibold px-1.5 py-0.5 rounded bg-indigo-50/80 text-indigo-600 border border-indigo-100">
                                                            {act.sourceType || 'Manual'}
                                                        </span>
                                                        {isDocAction && (
                                                            <span className="text-[10px] font-medium text-slate-400">• Evidence Action</span>
                                                        )}
                                                    </div>
                                                    <span className={`text-xs font-bold block leading-tight ${isActionCancelled ? 'text-slate-500 line-through' : 'text-slate-900'}`}>
                                                        {act.title}
                                                    </span>
                                                </div>

                                                {/* Verification or Completion Status Badge */}
                                                <div>
                                                    {isActionCancelled ? (
                                                        <span className="inline-flex items-center gap-1 text-[10px] font-bold text-slate-500 bg-slate-200 px-2 py-0.5 rounded-full border border-slate-300">
                                                            🚫 No longer required
                                                        </span>
                                                    ) : isActionVerified ? (
                                                        <span className="inline-flex items-center gap-1 text-[10px] font-bold text-emerald-700 bg-emerald-100/90 px-2 py-0.5 rounded-full border border-emerald-300">
                                                            <CheckCircle2 className="w-3 h-3" />
                                                            Verified
                                                        </span>
                                                    ) : isActionRejected ? (
                                                        <span className="inline-flex items-center gap-1 text-[10px] font-bold text-rose-700 bg-rose-100/90 px-2 py-0.5 rounded-full border border-rose-300">
                                                            <X className="w-3 h-3" />
                                                            Rejected
                                                        </span>
                                                    ) : act.status === 'Uploaded' ? (
                                                        <span className="inline-flex items-center gap-1 text-[10px] font-bold text-sky-700 bg-sky-50 px-2 py-0.5 rounded-full border border-sky-200">
                                                            <Clock className="w-3 h-3" />
                                                            Uploaded
                                                        </span>
                                                    ) : act.isCompleted || act.status === 'Completed' ? (
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
                                                    {!isActionVerified && !isActionCancelled && (
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
                                            ) : isDocAction && !isActionCancelled ? (
                                                <div className="p-2 rounded-lg bg-amber-50/60 border border-amber-200/60 text-[11px] text-amber-800 flex items-center gap-1.5">
                                                    <Clock className="w-3.5 h-3.5 text-amber-600 flex-shrink-0" />
                                                    <span>Awaiting client PDF upload in Client Portal</span>
                                                </div>
                                            ) : null}

                                            {/* General Task: Mark Complete button */}
                                            {!act.isCompleted && act.status !== 'Completed' && !isActionCancelled && !linkedDoc && (
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
                                        <option value="ProofOfAddress">Proof of Address</option>
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
            {/* CSTD-24: Add Condition Modal */}
            {isAddConditionOpen && (
                <div className="fixed inset-0 z-50 flex items-center justify-center p-4 bg-slate-900/60 backdrop-blur-xs overflow-y-auto">
                    <div className="bg-white rounded-2xl max-w-lg w-full p-6 shadow-2xl border border-slate-200 space-y-4 animate-in fade-in zoom-in-95 duration-150 my-8">
                        <div className="flex items-center justify-between pb-3 border-b border-slate-100">
                            <div className="flex items-center gap-2.5">
                                <div className="p-2 rounded-xl bg-violet-50 text-violet-600 border border-violet-100">
                                    <ShieldCheck className="w-5 h-5" />
                                </div>
                                <div>
                                    <h3 className="text-base font-bold text-slate-900">Attach Engagement Condition</h3>
                                    <p className="text-xs text-slate-500">Gate advancement to a target stage with an Approval or Payment prerequisite.</p>
                                </div>
                            </div>
                            <button
                                type="button"
                                onClick={() => setIsAddConditionOpen(false)}
                                className="p-1 rounded-lg text-slate-400 hover:text-slate-600 hover:bg-slate-100"
                            >
                                <X className="w-5 h-5" />
                            </button>
                        </div>

                        {createConditionError && (
                            <div className="p-3 bg-red-50 border border-red-200 rounded-xl text-red-700 text-xs font-semibold flex items-center gap-2">
                                <AlertTriangle className="w-4 h-4 shrink-0" />
                                <span>{createConditionError}</span>
                            </div>
                        )}

                        <form onSubmit={handleCreateCondition} className="space-y-3.5">
                            {/* Condition Type Selector */}
                            <div>
                                <label className="block text-xs font-bold text-slate-700 mb-1.5">Condition Type</label>
                                <div className="grid grid-cols-2 gap-2">
                                    <button
                                        type="button"
                                        onClick={() => setNewCondType('Approval')}
                                        className={`p-2.5 rounded-xl border text-xs font-bold flex items-center justify-center gap-2 transition ${
                                            newCondType === 'Approval'
                                                ? 'bg-violet-50 border-violet-300 text-violet-800 ring-2 ring-violet-500/20 shadow-xs'
                                                : 'bg-slate-50 border-slate-200 text-slate-600 hover:bg-slate-100'
                                        }`}
                                    >
                                        <CheckSquare className="w-4 h-4" />
                                        <span>Approval Gate</span>
                                    </button>
                                    <button
                                        type="button"
                                        onClick={() => setNewCondType('Payment')}
                                        className={`p-2.5 rounded-xl border text-xs font-bold flex items-center justify-center gap-2 transition ${
                                            newCondType === 'Payment'
                                                ? 'bg-emerald-50 border-emerald-300 text-emerald-800 ring-2 ring-emerald-500/20 shadow-xs'
                                                : 'bg-slate-50 border-slate-200 text-slate-600 hover:bg-slate-100'
                                        }`}
                                    >
                                        <CreditCard className="w-4 h-4" />
                                        <span>Payment Gate</span>
                                    </button>
                                </div>
                            </div>

                            {/* Target Gated Stage Dropdown */}
                            <div>
                                <label className="block text-xs font-bold text-slate-700 mb-1">
                                    Required Before Stage <span className="text-rose-500">*</span>
                                </label>
                                <select
                                    value={newCondStage}
                                    onChange={(e) => setNewCondStage(e.target.value as EngagementStage)}
                                    className="w-full text-xs bg-slate-50 border border-slate-200 rounded-xl p-2.5 text-slate-800 focus:outline-none focus:ring-2 focus:ring-violet-500/20 focus:border-violet-500"
                                >
                                    {ENGAGEMENT_STAGES.filter((s) => s.order > currentStageOrder).map((s) => (
                                        <option key={s.key} value={s.key}>
                                            Stage {s.order}: {s.name}
                                        </option>
                                    ))}
                                </select>
                                <p className="text-[11px] text-slate-400 mt-1">
                                    Stage advancement into this stage will be blocked until the condition is fulfilled.
                                </p>
                            </div>

                            {/* Title */}
                            <div>
                                <label className="block text-xs font-bold text-slate-700 mb-1">
                                    Condition Title <span className="text-rose-500">*</span>
                                </label>
                                <input
                                    type="text"
                                    required
                                    value={newCondTitle}
                                    onChange={(e) => setNewCondTitle(e.target.value)}
                                    placeholder={newCondType === 'Approval' ? 'E.g., Board Risk & Compliance Sign-Off' : 'E.g., Upfront Retainer Deposit'}
                                    className="w-full text-xs bg-slate-50 border border-slate-200 rounded-xl p-2.5 text-slate-800 focus:outline-none focus:ring-2 focus:ring-violet-500/20 focus:border-violet-500"
                                />
                            </div>

                            {/* Description */}
                            <div>
                                <label className="block text-xs font-bold text-slate-700 mb-1">Description / Client Instructions</label>
                                <textarea
                                    rows={2}
                                    value={newCondDesc}
                                    onChange={(e) => setNewCondDesc(e.target.value)}
                                    placeholder="Explain condition requirements or instructions shown to client..."
                                    className="w-full text-xs bg-slate-50 border border-slate-200 rounded-xl p-2.5 text-slate-800 focus:outline-none focus:ring-2 focus:ring-violet-500/20 focus:border-violet-500"
                                />
                            </div>

                            {/* Payment-specific fields */}
                            {newCondType === 'Payment' && (
                                <div className="p-3.5 bg-emerald-50/50 border border-emerald-200/80 rounded-xl space-y-3">
                                    <div className="grid grid-cols-1 sm:grid-cols-3 gap-3">
                                        <div className="sm:col-span-2">
                                            <label className="block text-xs font-bold text-slate-700 mb-1">
                                                Amount <span className="text-rose-500">*</span>
                                            </label>
                                            <input
                                                type="number"
                                                step="0.01"
                                                min="0.01"
                                                required
                                                value={newCondAmount}
                                                onChange={(e) => setNewCondAmount(e.target.value === '' ? '' : parseFloat(e.target.value))}
                                                placeholder="5000.00"
                                                className="w-full text-xs bg-white border border-slate-200 rounded-xl p-2.5 text-slate-800 focus:outline-none focus:ring-2 focus:ring-emerald-500/20 focus:border-emerald-500"
                                            />
                                        </div>
                                        <div>
                                            <label className="block text-xs font-bold text-slate-700 mb-1">
                                                Currency <span className="text-rose-500">*</span>
                                            </label>
                                            <input
                                                type="text"
                                                maxLength={3}
                                                required
                                                value={newCondCurrency}
                                                onChange={(e) => setNewCondCurrency(e.target.value.toUpperCase())}
                                                placeholder="USD"
                                                className="w-full text-xs bg-white border border-slate-200 rounded-xl p-2.5 text-slate-800 uppercase focus:outline-none focus:ring-2 focus:ring-emerald-500/20 focus:border-emerald-500"
                                            />
                                        </div>
                                    </div>

                                    <div>
                                        <label className="block text-xs font-bold text-slate-700 mb-1">Payment Schedule Type</label>
                                        <select
                                            value={newCondPaymentType}
                                            onChange={(e) => setNewCondPaymentType(e.target.value as ConditionPaymentType)}
                                            className="w-full text-xs bg-white border border-slate-200 rounded-xl p-2.5 text-slate-800 focus:outline-none focus:ring-2 focus:ring-emerald-500/20 focus:border-emerald-500"
                                        >
                                            <option value="Upfront">Upfront</option>
                                            <option value="Milestone">Milestone</option>
                                            <option value="Final">Final</option>
                                        </select>
                                    </div>
                                </div>
                            )}

                            {/* Due Date & Internal Note */}
                            <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
                                <div>
                                    <label className="block text-xs font-bold text-slate-700 mb-1">Due Date (Optional)</label>
                                    <input
                                        type="date"
                                        value={newCondDueDate}
                                        onChange={(e) => setNewCondDueDate(e.target.value)}
                                        className="w-full text-xs bg-slate-50 border border-slate-200 rounded-xl p-2.5 text-slate-800 focus:outline-none focus:ring-2 focus:ring-violet-500/20 focus:border-violet-500"
                                    />
                                </div>
                                <div>
                                    <label className="block text-xs font-bold text-slate-700 mb-1">
                                        Internal Note <span className="text-slate-400 font-normal">(Staff Only)</span>
                                    </label>
                                    <input
                                        type="text"
                                        value={newCondInternalNote}
                                        onChange={(e) => setNewCondInternalNote(e.target.value)}
                                        placeholder="Internal reference or notes..."
                                        className="w-full text-xs bg-slate-50 border border-slate-200 rounded-xl p-2.5 text-slate-800 focus:outline-none focus:ring-2 focus:ring-violet-500/20 focus:border-violet-500"
                                    />
                                </div>
                            </div>

                            <div className="flex items-center justify-end gap-3 pt-3 border-t border-slate-100">
                                <button
                                    type="button"
                                    onClick={() => setIsAddConditionOpen(false)}
                                    className="px-4 py-2 text-xs font-semibold text-slate-600 hover:text-slate-800 transition"
                                >
                                    Cancel
                                </button>
                                <button
                                    type="submit"
                                    disabled={isCreatingCondition || !newCondTitle.trim()}
                                    className="inline-flex items-center gap-2 px-5 py-2.5 rounded-xl text-xs font-bold text-white bg-gradient-to-r from-[#635bff] to-[#712ae2] hover:opacity-95 shadow-sm transition disabled:opacity-50"
                                >
                                    {isCreatingCondition ? (
                                        <>
                                            <Loader2 className="w-3.5 h-3.5 animate-spin" />
                                            <span>Attaching Condition...</span>
                                        </>
                                    ) : (
                                        <>
                                            <Plus className="w-4 h-4" />
                                            <span>Attach Condition</span>
                                        </>
                                    )}
                                </button>
                            </div>
                        </form>
                    </div>
                </div>
            )}

            {/* CSTD-24: Edit Condition Modal */}
            {conditionToEdit && (
                <div className="fixed inset-0 z-50 flex items-center justify-center p-4 bg-slate-900/60 backdrop-blur-xs overflow-y-auto">
                    <div className="bg-white rounded-2xl max-w-lg w-full p-6 shadow-2xl border border-slate-200 space-y-4 animate-in fade-in zoom-in-95 duration-150 my-8">
                        <div className="flex items-center justify-between pb-3 border-b border-slate-100">
                            <div className="flex items-center gap-2.5">
                                <div className="p-2 rounded-xl bg-indigo-50 text-indigo-600 border border-indigo-100">
                                    <Edit3 className="w-5 h-5" />
                                </div>
                                <div>
                                    <h3 className="text-base font-bold text-slate-900">Edit Condition</h3>
                                    <p className="text-xs text-slate-500">Update parameters for this pending condition.</p>
                                </div>
                            </div>
                            <button
                                type="button"
                                onClick={() => setConditionToEdit(null)}
                                className="p-1 rounded-lg text-slate-400 hover:text-slate-600 hover:bg-slate-100"
                            >
                                <X className="w-5 h-5" />
                            </button>
                        </div>

                        {updateConditionError && (
                            <div className="p-3 bg-red-50 border border-red-200 rounded-xl text-red-700 text-xs font-semibold flex items-center gap-2">
                                <AlertTriangle className="w-4 h-4 shrink-0" />
                                <span>{updateConditionError}</span>
                            </div>
                        )}

                        <form onSubmit={handleUpdateCondition} className="space-y-3.5">
                            {/* Non-editable metadata badges */}
                            <div className="p-2.5 rounded-xl bg-slate-50 border border-slate-200 flex items-center gap-3 text-xs">
                                <span className="font-semibold text-slate-600">Type: <span className="font-bold text-slate-800">{conditionToEdit.type}</span></span>
                                <span className="text-slate-300">•</span>
                                <span className="font-semibold text-slate-600">Gating Stage: <span className="font-bold text-slate-800">{conditionToEdit.requiredBeforeStage}</span></span>
                            </div>

                            {/* Title */}
                            <div>
                                <label className="block text-xs font-bold text-slate-700 mb-1">
                                    Condition Title <span className="text-rose-500">*</span>
                                </label>
                                <input
                                    type="text"
                                    required
                                    value={editCondTitle}
                                    onChange={(e) => setEditCondTitle(e.target.value)}
                                    className="w-full text-xs bg-slate-50 border border-slate-200 rounded-xl p-2.5 text-slate-800 focus:outline-none focus:ring-2 focus:ring-indigo-500/20 focus:border-indigo-500"
                                />
                            </div>

                            {/* Description */}
                            <div>
                                <label className="block text-xs font-bold text-slate-700 mb-1">Description</label>
                                <textarea
                                    rows={2}
                                    value={editCondDesc}
                                    onChange={(e) => setEditCondDesc(e.target.value)}
                                    className="w-full text-xs bg-slate-50 border border-slate-200 rounded-xl p-2.5 text-slate-800 focus:outline-none focus:ring-2 focus:ring-indigo-500/20 focus:border-indigo-500"
                                />
                            </div>

                            {/* Payment fields if Payment condition */}
                            {conditionToEdit.type === 'Payment' && (
                                <div className="p-3.5 bg-emerald-50/50 border border-emerald-200/80 rounded-xl space-y-3">
                                    <div className="grid grid-cols-1 sm:grid-cols-3 gap-3">
                                        <div className="sm:col-span-2">
                                            <label className="block text-xs font-bold text-slate-700 mb-1">Amount</label>
                                            <input
                                                type="number"
                                                step="0.01"
                                                min="0.01"
                                                value={editCondAmount}
                                                onChange={(e) => setEditCondAmount(e.target.value === '' ? '' : parseFloat(e.target.value))}
                                                className="w-full text-xs bg-white border border-slate-200 rounded-xl p-2.5 text-slate-800 focus:outline-none focus:ring-2 focus:ring-emerald-500/20 focus:border-emerald-500"
                                            />
                                        </div>
                                        <div>
                                            <label className="block text-xs font-bold text-slate-700 mb-1">Currency</label>
                                            <input
                                                type="text"
                                                maxLength={3}
                                                value={editCondCurrency}
                                                onChange={(e) => setEditCondCurrency(e.target.value.toUpperCase())}
                                                className="w-full text-xs bg-white border border-slate-200 rounded-xl p-2.5 text-slate-800 uppercase focus:outline-none focus:ring-2 focus:ring-emerald-500/20 focus:border-emerald-500"
                                            />
                                        </div>
                                    </div>

                                    <div>
                                        <label className="block text-xs font-bold text-slate-700 mb-1">Payment Schedule Type</label>
                                        <select
                                            value={editCondPaymentType}
                                            onChange={(e) => setEditCondPaymentType(e.target.value as ConditionPaymentType)}
                                            className="w-full text-xs bg-white border border-slate-200 rounded-xl p-2.5 text-slate-800 focus:outline-none focus:ring-2 focus:ring-emerald-500/20 focus:border-emerald-500"
                                        >
                                            <option value="Upfront">Upfront</option>
                                            <option value="Milestone">Milestone</option>
                                            <option value="Final">Final</option>
                                        </select>
                                    </div>
                                </div>
                            )}

                            {/* Due Date & Internal Note */}
                            <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
                                <div>
                                    <label className="block text-xs font-bold text-slate-700 mb-1">Due Date</label>
                                    <input
                                        type="date"
                                        value={editCondDueDate}
                                        onChange={(e) => setEditCondDueDate(e.target.value)}
                                        className="w-full text-xs bg-slate-50 border border-slate-200 rounded-xl p-2.5 text-slate-800 focus:outline-none focus:ring-2 focus:ring-indigo-500/20 focus:border-indigo-500"
                                    />
                                </div>
                                <div>
                                    <label className="block text-xs font-bold text-slate-700 mb-1">
                                        Internal Note <span className="text-slate-400 font-normal">(Staff Only)</span>
                                    </label>
                                    <input
                                        type="text"
                                        value={editCondInternalNote}
                                        onChange={(e) => setEditCondInternalNote(e.target.value)}
                                        className="w-full text-xs bg-slate-50 border border-slate-200 rounded-xl p-2.5 text-slate-800 focus:outline-none focus:ring-2 focus:ring-indigo-500/20 focus:border-indigo-500"
                                    />
                                </div>
                            </div>

                            <div className="flex items-center justify-end gap-3 pt-3 border-t border-slate-100">
                                <button
                                    type="button"
                                    onClick={() => setConditionToEdit(null)}
                                    className="px-4 py-2 text-xs font-semibold text-slate-600 hover:text-slate-800 transition"
                                >
                                    Cancel
                                </button>
                                <button
                                    type="submit"
                                    disabled={isUpdatingCondition || !editCondTitle.trim()}
                                    className="inline-flex items-center gap-2 px-5 py-2.5 rounded-xl text-xs font-bold text-white bg-indigo-600 hover:bg-indigo-700 shadow-sm transition disabled:opacity-50"
                                >
                                    {isUpdatingCondition ? (
                                        <>
                                            <Loader2 className="w-3.5 h-3.5 animate-spin" />
                                            <span>Saving Changes...</span>
                                        </>
                                    ) : (
                                        <span>Save Changes</span>
                                    )}
                                </button>
                            </div>
                        </form>
                    </div>
                </div>
            )}

            {/* CSTD-24: Deactivate Condition Modal */}
            {conditionToDeactivate && (
                <div className="fixed inset-0 z-50 flex items-center justify-center p-4 bg-slate-900/60 backdrop-blur-xs">
                    <div className="bg-white rounded-2xl max-w-md w-full p-6 shadow-2xl border border-slate-200 space-y-4 animate-in fade-in zoom-in-95 duration-150">
                        <div className="flex items-center justify-between pb-3 border-b border-slate-100">
                            <div className="flex items-center gap-2.5">
                                <div className="p-2 rounded-xl bg-rose-50 text-rose-600 border border-rose-100">
                                    <Trash2 className="w-5 h-5" />
                                </div>
                                <div>
                                    <h3 className="text-base font-bold text-slate-900">Deactivate Condition</h3>
                                    <p className="text-xs text-slate-500">Remove this condition from gating pipeline stages.</p>
                                </div>
                            </div>
                            <button
                                type="button"
                                onClick={() => setConditionToDeactivate(null)}
                                className="p-1 rounded-lg text-slate-400 hover:text-slate-600 hover:bg-slate-100"
                            >
                                <X className="w-5 h-5" />
                            </button>
                        </div>

                        {deactivateConditionError && (
                            <div className="p-3 bg-red-50 border border-red-200 rounded-xl text-red-700 text-xs font-semibold flex items-center gap-2">
                                <AlertTriangle className="w-4 h-4 shrink-0" />
                                <span>{deactivateConditionError}</span>
                            </div>
                        )}

                        <div className="p-3.5 rounded-xl bg-amber-50 border border-amber-200 text-xs text-amber-900 space-y-1">
                            <div className="font-bold flex items-center gap-1.5">
                                <AlertCircle className="w-4 h-4 text-amber-600 shrink-0" />
                                <span>Warning: Immediate Gate Removal</span>
                            </div>
                            <p className="text-[11px] text-amber-800 leading-relaxed">
                                Deactivating <strong>"{conditionToDeactivate.title}"</strong> will immediately cancel any linked client deliverables and remove this gate blocker.
                            </p>
                        </div>

                        <form onSubmit={handleConfirmDeactivateCondition} className="space-y-3.5">
                            <div>
                                <label className="block text-xs font-bold text-slate-700 mb-1">
                                    Deactivation Reason <span className="text-rose-500">*</span>
                                </label>
                                <textarea
                                    rows={3}
                                    required
                                    value={deactivateReason}
                                    onChange={(e) => setDeactivateReason(e.target.value)}
                                    placeholder="Explain why this condition is being deactivated (recorded in immutable audit log)..."
                                    className="w-full text-xs bg-slate-50 border border-slate-200 rounded-xl p-2.5 text-slate-800 focus:outline-none focus:ring-2 focus:ring-rose-500/20 focus:border-rose-500"
                                />
                            </div>

                            <div className="flex items-center justify-end gap-3 pt-3 border-t border-slate-100">
                                <button
                                    type="button"
                                    onClick={() => setConditionToDeactivate(null)}
                                    className="px-4 py-2 text-xs font-semibold text-slate-600 hover:text-slate-800 transition"
                                >
                                    Cancel
                                </button>
                                <button
                                    type="submit"
                                    disabled={isDeactivatingCondition || !deactivateReason.trim()}
                                    className="inline-flex items-center gap-2 px-5 py-2.5 rounded-xl text-xs font-bold text-white bg-rose-600 hover:bg-rose-700 shadow-sm transition disabled:opacity-50"
                                >
                                    {isDeactivatingCondition ? (
                                        <>
                                            <Loader2 className="w-3.5 h-3.5 animate-spin" />
                                            <span>Deactivating...</span>
                                        </>
                                    ) : (
                                        <>
                                            <Trash2 className="w-4 h-4" />
                                            <span>Confirm Deactivation</span>
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
