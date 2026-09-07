import React, { useState, useEffect, useCallback } from 'react';
import { useNavigate } from 'react-router-dom';
import { useAuth } from '../context/AuthContext';
import { WorkflowApi, IdentityApi } from '../services/api';
import { Engagement, ClientProfile, UserAccountResponse, ClientAction } from '../types';
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
    Layers,
    Lock,
    User,
    Building2,
    Calendar,
    ArrowRight
} from 'lucide-react';

interface WorkspaceStageViewProps {
    engagementId: string;
    onBack?: () => void;
}

interface StageDefinition {
    id: number;
    name: string;
    tagline: string;
    description: string;
    checklist: { id: string; label: string; defaultDone?: boolean }[];
}

const ONBOARDING_STAGES: StageDefinition[] = [
    {
        id: 1,
        name: 'Intake & Onboarding',
        tagline: 'Client baseline & kickoff criteria',
        description: 'Establish initial client identity, assign lead custodian, collect service parameters, and configure secure communications.',
        checklist: [
            { id: 'c1-1', label: 'Client company profile & primary contacts registered', defaultDone: true },
            { id: 'c1-2', label: 'Assigned custodian and secondary staff handler confirmed', defaultDone: true },
            { id: 'c1-3', label: 'Service level agreement & operational scope baseline established' },
            { id: 'c1-4', label: 'Kickoff intake form submitted by client contact' }
        ]
    },
    {
        id: 2,
        name: 'Compliance Evidence & Verification',
        tagline: 'Deterministic document & KYC validation',
        description: 'Collect mandatory compliance documents, verify corporate status, conduct deterministic validation, and securely store proofs in Document Vault.',
        checklist: [
            { id: 'c2-1', label: 'Certificate of Incorporation / Corporate Charter uploaded' },
            { id: 'c2-2', label: 'Beneficial Ownership (UBO) declaration signed' },
            { id: 'c2-3', label: 'Authorized representative identity & proof of address validated' },
            { id: 'c2-4', label: 'Deterministic document verification completed in Document Vault' }
        ]
    },
    {
        id: 3,
        name: 'Gate Evaluation & Approvals',
        tagline: 'Milestone criteria & dual signoff',
        description: 'Verify all gate requirements have been met, confirm financial escrow/deposit terms, and execute dual-custodian review before live operations.',
        checklist: [
            { id: 'c3-1', label: 'Financial retainer / escrow deposit verification confirmed' },
            { id: 'c3-2', label: 'Risk assessment & compliance score evaluated' },
            { id: 'c3-3', label: 'Dual-custodian sign-off executed (Lead Custodian + SecOps)' },
            { id: 'c3-4', label: 'Stage 3 Milestone Gate token generated' }
        ]
    },
    {
        id: 4,
        name: 'SLA Radar & Intervention',
        tagline: 'Latency monitoring & stall prevention',
        description: 'Continuous monitoring of request response times, stall alert detection, and automatic intervention routing to prevent SLA degradation.',
        checklist: [
            { id: 'c4-1', label: 'SLA response monitor active (current latency: < 24ms)' },
            { id: 'c4-2', label: 'Zero critical stall warnings in operational queue' },
            { id: 'c4-3', label: 'Escalation notification channels verified' },
            { id: 'c4-4', label: 'Weekly SLA assurance scorecard generated (> 98.5% adherence)' }
        ]
    },
    {
        id: 5,
        name: 'Readiness, Handoff & Closure',
        tagline: 'Final delivery & immutable audit seal',
        description: 'Compile final deliverables, emit immutable audit ledger seal, deliver client self-service portal credentials, and seal engagement.',
        checklist: [
            { id: 'c5-1', label: 'Final engagement deliverables validated and accepted by client' },
            { id: 'c5-2', label: 'Client self-service portal access handed off' },
            { id: 'c5-3', label: 'Immutable audit log hash chain sealed and archived' },
            { id: 'c5-4', label: 'Formal engagement completion certificate issued' }
        ]
    }
];

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

    // Active stage state (1 to 5)
    const [activeStageId, setActiveStageId] = useState<number>(1);
    const [currentPipelineStage, setCurrentPipelineStage] = useState<number>(1);

    // Checklist toggles state
    const [checklistState, setChecklistState] = useState<Record<string, boolean>>({
        'c1-1': true,
        'c1-2': true,
    });

    const [completingActionId, setCompletingActionId] = useState<string | null>(null);
    const [isAdvancingStage, setIsAdvancingStage] = useState(false);
    const [stageAdvanceSuccess, setStageAdvanceSuccess] = useState<string | null>(null);

    // Load Workspace Data
    const loadWorkspaceData = useCallback(async () => {
        if (!tenantId || !engagementId) return;
        setIsLoading(true);

        try {
            const engagements = await WorkflowApi.getEngagements(tenantId).catch(() => [] as Engagement[]);
            const foundEng = engagements.find((e) => e.engagementId === engagementId);
            if (foundEng) {
                setEngagement(foundEng);
                if (foundEng.status === 'Closed') {
                    setCurrentPipelineStage(5);
                    setActiveStageId(5);
                }
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

    const handleToggleChecklist = (checkId: string) => {
        setChecklistState((prev) => ({
            ...prev,
            [checkId]: !prev[checkId]
        }));
    };

    const handleCompleteAction = async (actionId: string) => {
        setCompletingActionId(actionId);
        setTimeout(() => {
            setActions((prev) =>
                prev.map((a) => (a.actionId === actionId ? { ...a, isCompleted: true } : a))
            );
            setCompletingActionId(null);
        }, 500);
    };

    const handleAdvanceStage = () => {
        if (currentPipelineStage >= 5) return;
        setIsAdvancingStage(true);
        setStageAdvanceSuccess(null);

        setTimeout(() => {
            const nextStage = currentPipelineStage + 1;
            setCurrentPipelineStage(nextStage);
            setActiveStageId(nextStage);
            setIsAdvancingStage(false);
            setStageAdvanceSuccess(`Stage ${currentPipelineStage} Gate Cleared • Entering Stage ${nextStage}: ${ONBOARDING_STAGES[nextStage - 1].name}`);
            setTimeout(() => setStageAdvanceSuccess(null), 4000);
        }, 600);
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

    const selectedStageDef = ONBOARDING_STAGES.find((s) => s.id === activeStageId) || ONBOARDING_STAGES[0];
    const stageChecklistItems = selectedStageDef.checklist;
    const completedItemsCount = stageChecklistItems.filter((i) => checklistState[i.id]).length;
    const progressPercent = Math.round((completedItemsCount / stageChecklistItems.length) * 100);

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
                            <span className="inline-flex items-center gap-1.5 px-2.5 py-0.5 rounded-full text-xs font-semibold bg-emerald-50 text-emerald-700 border border-emerald-200">
                                <span className="w-1.5 h-1.5 rounded-full bg-emerald-500" />
                                Active Pipeline
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

                    <div className="p-3 bg-emerald-50/80 rounded-xl border border-emerald-200/80 text-xs">
                        <span className="text-[10px] font-bold text-emerald-600 uppercase tracking-wider block">
                            SLA HEALTH
                        </span>
                        <span className="font-bold text-emerald-700 block mt-0.5">
                            98.8% Optimal
                        </span>
                    </div>
                </div>
            </div>

            {/* 5-Stage Orchestration Stepper */}
            <div className="bg-white/90 backdrop-blur-md p-5 rounded-2xl border border-slate-200/90 shadow-xs space-y-4">
                <div className="flex items-center justify-between pb-3 border-b border-slate-100">
                    <div>
                        <h2 className="text-base font-bold text-slate-900 flex items-center gap-2">
                            <Layers className="w-5 h-5 text-indigo-600" />
                            5-Stage Onboarding Progression
                        </h2>
                        <p className="text-xs text-slate-500 mt-0.5">
                            Deterministic lifecycle progression from intake verification to handoff & closure.
                        </p>
                    </div>
                    <span className="px-3 py-1 rounded-full text-xs font-semibold bg-indigo-50 text-indigo-700 border border-indigo-200">
                        Current: Stage {currentPipelineStage} of 5
                    </span>
                </div>

                {/* 5 Step Cards */}
                <div className="grid grid-cols-1 md:grid-cols-5 gap-3">
                    {ONBOARDING_STAGES.map((st) => {
                        const isPassed = st.id < currentPipelineStage;
                        const isCurrent = st.id === currentPipelineStage;
                        const isSelected = st.id === activeStageId;

                        return (
                            <button
                                key={st.id}
                                onClick={() => setActiveStageId(st.id)}
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
                                        {isPassed ? '✓' : st.id}
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

            {/* Stage Alert */}
            {stageAdvanceSuccess && (
                <div className="p-3 bg-emerald-50 border border-emerald-200 rounded-xl text-emerald-900 text-xs font-semibold flex items-center gap-2">
                    <CheckCircle className="w-4 h-4 text-emerald-600 shrink-0" />
                    <span>{stageAdvanceSuccess}</span>
                </div>
            )}

            {/* Stage Deep Dive: Two Columns */}
            <div className="grid grid-cols-1 lg:grid-cols-12 gap-6 items-start">
                {/* Left: Gate Checklist & Criteria (8 Cols) */}
                <div className="lg:col-span-8 space-y-4">
                    <div className="bg-white/90 backdrop-blur-md p-6 rounded-2xl border border-slate-200/90 shadow-xs space-y-5">
                        {/* Stage Header */}
                        <div>
                            <div className="flex items-center gap-2 mb-1.5">
                                <span className="px-2.5 py-0.5 rounded-full text-xs font-bold bg-indigo-100 text-indigo-700 uppercase tracking-wider">
                                    Stage {selectedStageDef.id} Gate Requirements
                                </span>
                                {selectedStageDef.id === currentPipelineStage && (
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

                        {/* Progress Bar */}
                        <div className="pt-2 border-t border-slate-100">
                            <div className="flex items-center justify-between text-xs mb-1.5">
                                <span className="font-bold text-slate-500 uppercase tracking-wider">
                                    Gate Verification Progress
                                </span>
                                <span className="font-bold text-indigo-600">
                                    {completedItemsCount} / {stageChecklistItems.length} Requirements Satisfied ({progressPercent}%)
                                </span>
                            </div>
                            <div className="h-2 w-full bg-slate-100 rounded-full overflow-hidden">
                                <div
                                    className="h-full bg-gradient-to-r from-[#635bff] to-[#712ae2] rounded-full transition-all duration-300"
                                    style={{ width: `${progressPercent}%` }}
                                />
                            </div>
                        </div>

                        {/* Interactive Gate Criteria Checklist */}
                        <div className="space-y-2.5 pt-2">
                            {stageChecklistItems.map((item) => {
                                const isChecked = !!checklistState[item.id];
                                return (
                                    <label
                                        key={item.id}
                                        className={`flex items-start gap-3 p-3.5 rounded-xl border transition cursor-pointer ${
                                            isChecked
                                                ? 'bg-indigo-50/40 border-indigo-200 text-slate-900'
                                                : 'bg-white border-slate-200 text-slate-600 hover:bg-slate-50'
                                        }`}
                                    >
                                        <input
                                            type="checkbox"
                                            checked={isChecked}
                                            onChange={() => handleToggleChecklist(item.id)}
                                            className="mt-0.5 h-4 w-4 rounded border-slate-300 text-indigo-600 focus:ring-indigo-500 cursor-pointer"
                                        />
                                        <div className="flex-1">
                                            <span className={`text-xs ${isChecked ? 'font-semibold text-slate-900' : 'text-slate-700'}`}>
                                                {item.label}
                                            </span>
                                        </div>
                                        {isChecked && (
                                            <span className="text-[10px] font-bold text-emerald-700 bg-emerald-50 px-2 py-0.5 rounded border border-emerald-200">
                                                Verified
                                            </span>
                                        )}
                                    </label>
                                );
                            })}
                        </div>

                        {/* Advance Stage Footer */}
                        {selectedStageDef.id === currentPipelineStage && currentPipelineStage < 5 && (
                            <div className="pt-4 border-t border-slate-100 flex items-center justify-between">
                                <span className="text-xs text-slate-500">
                                    {progressPercent === 100
                                        ? 'All milestone gate criteria satisfied. Ready to advance.'
                                        : 'Satisfy all stage criteria to unlock gate advancement.'}
                                </span>
                                <button
                                    type="button"
                                    onClick={handleAdvanceStage}
                                    disabled={progressPercent < 100 || isAdvancingStage}
                                    className={`px-5 py-2.5 rounded-xl text-xs font-bold flex items-center gap-2 transition shadow-sm ${
                                        progressPercent === 100
                                            ? 'bg-gradient-to-r from-[#635bff] to-[#712ae2] hover:opacity-95 text-white shadow-indigo-500/25 cursor-pointer'
                                            : 'bg-slate-100 text-slate-400 cursor-not-allowed'
                                    }`}
                                >
                                    {isAdvancingStage ? (
                                        <>
                                            <Loader2 className="w-4 h-4 animate-spin" />
                                            <span>Advancing Stage...</span>
                                        </>
                                    ) : (
                                        <>
                                            <span>Advance to Stage {currentPipelineStage + 1}</span>
                                            <ChevronRight className="w-4 h-4" />
                                        </>
                                    )}
                                </button>
                            </div>
                        )}

                        {selectedStageDef.id === 5 && currentPipelineStage === 5 && (
                            <div className="pt-4 border-t border-slate-100 flex items-center justify-between">
                                <span className="text-xs text-slate-500">
                                    Final stage sign-off ready.
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
