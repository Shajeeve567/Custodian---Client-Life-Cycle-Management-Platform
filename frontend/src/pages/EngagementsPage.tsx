import React, { useState, useEffect, useCallback } from 'react';
import { useNavigate } from 'react-router-dom';
import { useAuth } from '../context/AuthContext';
import { IdentityApi, WorkflowApi, ApiError } from '../services/api';
import { Engagement, ClientProfile, UserAccountResponse } from '../types';
import { getStageDefinition } from '../constants/engagementStages';
import { DashboardLayout } from '../components/DashboardLayout';
import { TeamManagementModal } from '../components/TeamManagementModal';
import { EngagementInspectionModal } from '../components/EngagementInspectionModal';
import {
    Plus,
    Building2,
    User,
    Mail,
    Phone,
    Calendar,
    CheckCircle,
    X,
    Loader2,
    Search,
    Layers,
    Shield,
    Activity,
    Users,
    Briefcase,
    CheckCircle2,
    ArrowRight
} from 'lucide-react';

interface DisplayWorkspaceItem {
    id: string;
    clientName: string;
    code: string;
    title: string;
    initials: string;
    status: string;
    nextAction: string;
    custodians: string;
    slaScore: string;
    stageOrder: number;
    stageName: string;
    stageProgressPercentage: number;
    createdAt: string;
    rawEngagement: Engagement;
}

export const EngagementsPage: React.FC = () => {
    const navigate = useNavigate();
    const { token, tenantId, tenantName, userId, role } = useAuth();

    // Data states
    const [engagements, setEngagements] = useState<Engagement[]>([]);
    const [clients, setClients] = useState<ClientProfile[]>([]);
    const [teamMembers, setTeamMembers] = useState<UserAccountResponse[]>([]);
    const [isLoadingData, setIsLoadingData] = useState(true);

    // Search & Filter
    const [searchQuery, setSearchQuery] = useState('');
    const [statusFilter, setStatusFilter] = useState<'All' | 'Started' | 'Closed'>('All');

    // Modals
    const [isCreateModalOpen, setIsCreateModalOpen] = useState(false);
    const [isTeamModalOpen, setIsTeamModalOpen] = useState(false);
    const [inspectedEngagement, setInspectedEngagement] = useState<Engagement | null>(null);

    // Create Engagement Form
    const [isCreatingEngagement, setIsCreatingEngagement] = useState(false);
    const [selectedClientId, setSelectedClientId] = useState('');
    const [selectedStaffId, setSelectedStaffId] = useState('');
    const [engagementError, setEngagementError] = useState<string | null>(null);
    const [engagementSuccess, setEngagementSuccess] = useState<string | null>(null);

    // Inline Client creation
    const [showNewClientForm, setShowNewClientForm] = useState(false);
    const [isCreatingClient, setIsCreatingClient] = useState(false);
    const [newClientName, setNewClientName] = useState('');
    const [newClientEmail, setNewClientEmail] = useState('');
    const [newClientPhone, setNewClientPhone] = useState('');
    const [clientError, setClientError] = useState<string | null>(null);

    // Load API Data
    const loadAllData = useCallback(async () => {
        if (!tenantId) return;
        setIsLoadingData(true);

        try {
            const clientsPromise = IdentityApi.getClients(token || undefined).catch(() => [] as ClientProfile[]);
            const engagementsPromise = WorkflowApi.getEngagements(tenantId).catch(() => [] as Engagement[]);
            const teamPromise = token ? IdentityApi.getUsers(token).catch(() => [] as UserAccountResponse[]) : Promise.resolve([]);

            const [fetchedClients, fetchedEngagements, fetchedTeam] = await Promise.all([
                clientsPromise,
                engagementsPromise,
                teamPromise
            ]);

            setClients(fetchedClients);
            setEngagements(fetchedEngagements);
            setTeamMembers(fetchedTeam);
        } catch (err) {
            console.error('Data load error:', err);
        } finally {
            setIsLoadingData(false);
        }
    }, [tenantId, token]);

    useEffect(() => {
        loadAllData();
    }, [loadAllData]);

    // Inline Client creation
    const handleCreateClient = async (e: React.FormEvent) => {
        e.preventDefault();
        setClientError(null);

        if (!newClientName.trim() || !newClientEmail.trim()) {
            setClientError('Client name and email are required.');
            return;
        }

        setIsCreatingClient(true);
        try {
            const created = await IdentityApi.createClient(
                {
                    name: newClientName.trim(),
                    email: newClientEmail.trim(),
                    phone: newClientPhone.trim() || undefined,
                },
                token || undefined
            );

            setClients((prev) => [created, ...prev]);
            setSelectedClientId(created.id);
            setNewClientName('');
            setNewClientEmail('');
            setNewClientPhone('');
            setShowNewClientForm(false);
        } catch (err: any) {
            setClientError(err?.message || 'Failed to create client profile.');
        } finally {
            setIsCreatingClient(false);
        }
    };

    // Create Engagement
    const handleCreateEngagement = async (e: React.FormEvent) => {
        e.preventDefault();
        setEngagementError(null);
        setEngagementSuccess(null);

        if (!tenantId || !selectedClientId) {
            setEngagementError('Please select or add a target client.');
            return;
        }

        setIsCreatingEngagement(true);
        try {
            const created = await WorkflowApi.createEngagement({
                tenantId,
                clientId: selectedClientId,
                staffId: selectedStaffId || userId || 'staff-admin',
            });

            setEngagementSuccess('Workspace provisioned successfully!');
            const updated = await WorkflowApi.getEngagements(tenantId);
            setEngagements(updated);

            setTimeout(() => {
                setIsCreateModalOpen(false);
                setSelectedClientId('');
                setEngagementSuccess(null);
                setInspectedEngagement(created);
            }, 1000);
        } catch (err: any) {
            if (err instanceof ApiError) {
                setEngagementError(err.message);
            } else {
                setEngagementError(err?.message || 'Failed to create workspace engagement.');
            }
        } finally {
            setIsCreatingEngagement(false);
        }
    };

    // Transform real backend engagements into display format
    const allWorkspaces: DisplayWorkspaceItem[] = engagements.map((eng) => {
        const client = clients.find((c) => c.id === eng.clientId);
        const name = client?.name || `Client ${eng.clientId.slice(0, 8)}`;
        const initials = name
            .split(' ')
            .map((n) => n[0])
            .filter(Boolean)
            .join('')
            .slice(0, 2)
            .toUpperCase() || 'CL';
        const code = `ENG-${eng.engagementId.slice(0, 6).toUpperCase()}`;

        const staffHandler = teamMembers.find((m) => m.id === eng.staffId);
        const custodianLabel = staffHandler?.email ? staffHandler.email.split('@')[0] : (eng.staffId ? `${eng.staffId.slice(0, 10)}...` : 'Unassigned');
        const stageDef = getStageDefinition(eng.stage);

        return {
            id: eng.engagementId,
            clientName: name,
            code,
            title: 'Client Onboarding & Compliance Architecture',
            initials,
            status: eng.status,
            nextAction: eng.status === 'Closed' ? 'COMPLETED' : `STAGE: ${stageDef.name.toUpperCase()}`,
            custodians: custodianLabel,
            slaScore: '98.5%',
            stageOrder: stageDef.order,
            stageName: stageDef.name,
            stageProgressPercentage: eng.stageProgressPercentage,
            createdAt: new Date(eng.createdAt).toLocaleDateString(),
            rawEngagement: eng
        };
    });

    const filteredWorkspaces = allWorkspaces.filter((w) => {
        const query = searchQuery.toLowerCase();
        const matchesQuery =
            w.clientName.toLowerCase().includes(query) ||
            w.code.toLowerCase().includes(query) ||
            w.title.toLowerCase().includes(query) ||
            w.custodians.toLowerCase().includes(query);

        if (!matchesQuery) return false;
        if (statusFilter !== 'All' && w.status !== statusFilter) return false;
        return true;
    });

    const handleOpenWorkspaceModal = (ws: DisplayWorkspaceItem) => {
        setInspectedEngagement(ws.rawEngagement);
    };

    return (
        <DashboardLayout>
            <div className="space-y-8">
                {/* 1. Page Header */}
                <div className="flex flex-col sm:flex-row sm:items-center justify-between gap-4 pb-6 border-b border-slate-200/80">
                    <div>
                        <div className="telemetry-pill mb-2">
                            <span className="telemetry-dot" />
                            <span className="telemetry-text">{tenantName ? `${tenantName.toUpperCase()} • OPERATIONAL WORKSPACE` : 'OPERATIONAL ENGAGEMENT CONSOLE'}</span>
                        </div>
                        <h1 className="text-3xl font-bold text-slate-900 tracking-tight">
                            {tenantName ? `${tenantName}'s Workspace` : 'Agency Engagements & Workspaces'}
                        </h1>
                        <p className="text-sm text-slate-500 mt-1">
                            Client onboarding lifecycles, lead custodian assignment, and stage progression.
                        </p>
                    </div>

                    <div className="flex items-center gap-3 shrink-0">
                        {role === 'Owner' && (
                            <button
                                type="button"
                                onClick={() => setIsTeamModalOpen(true)}
                                className="px-4 py-2.5 rounded-xl border border-slate-200/80 bg-white/80 hover:bg-slate-50 text-slate-700 text-sm font-semibold flex items-center gap-2 shadow-xs transition backdrop-blur-xs"
                            >
                                <Users className="w-4 h-4 text-slate-500" />
                                <span>Manage Team</span>
                            </button>
                        )}

                        <button
                            type="button"
                            onClick={() => {
                                setIsCreateModalOpen(true);
                                setEngagementError(null);
                                setEngagementSuccess(null);
                            }}
                            className="px-5 py-2.5 rounded-xl bg-gradient-to-r from-[#635bff] to-[#712ae2] hover:opacity-95 text-white text-sm font-semibold flex items-center gap-2 shadow-sm shadow-indigo-500/25 transition"
                        >
                            <Plus className="w-4 h-4" />
                            <span>New Engagement</span>
                        </button>
                    </div>
                </div>

                {/* 2. Top Summary KPI Cards */}
                <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-4">
                    <div className="bg-white/85 backdrop-blur-md p-5 rounded-2xl border border-slate-200/80 shadow-xs space-y-2">
                        <div className="flex items-center justify-between text-slate-500">
                            <span className="text-[11px] font-bold text-slate-500 uppercase tracking-wider">Active Workspaces</span>
                            <div className="p-2 rounded-xl bg-indigo-50 text-indigo-600">
                                <Briefcase className="w-4 h-4" />
                            </div>
                        </div>
                        <div className="text-2xl font-bold text-slate-900">{allWorkspaces.length}</div>
                        <p className="text-xs text-slate-500">Live client pipelines</p>
                    </div>

                    <div className="bg-white/85 backdrop-blur-md p-5 rounded-2xl border border-slate-200/80 shadow-xs space-y-2">
                        <div className="flex items-center justify-between text-slate-500">
                            <span className="text-[11px] font-bold text-slate-500 uppercase tracking-wider">Client Records</span>
                            <div className="p-2 rounded-xl bg-indigo-50 text-indigo-600">
                                <Building2 className="w-4 h-4" />
                            </div>
                        </div>
                        <div className="text-2xl font-bold text-slate-900">{clients.length}</div>
                        <p className="text-xs text-slate-500">Registered client profiles</p>
                    </div>

                    <div className="bg-white/85 backdrop-blur-md p-5 rounded-2xl border border-slate-200/80 shadow-xs space-y-2">
                        <div className="flex items-center justify-between text-slate-500">
                            <span className="text-[11px] font-bold text-slate-500 uppercase tracking-wider">Staff Custodians</span>
                            <div className="p-2 rounded-xl bg-indigo-50 text-indigo-600">
                                <Users className="w-4 h-4" />
                            </div>
                        </div>
                        <div className="text-2xl font-bold text-slate-900">{teamMembers.length}</div>
                        <p className="text-xs text-slate-500">Active assigned handlers</p>
                    </div>

                    <div className="bg-white/85 backdrop-blur-md p-5 rounded-2xl border border-slate-200/80 shadow-xs space-y-2">
                        <div className="flex items-center justify-between text-slate-500">
                            <span className="text-[11px] font-bold text-slate-500 uppercase tracking-wider">SLA Assurance</span>
                            <div className="p-2 rounded-xl bg-emerald-50 text-emerald-600">
                                <CheckCircle2 className="w-4 h-4" />
                            </div>
                        </div>
                        <div className="text-2xl font-bold text-emerald-600">98.8%</div>
                        <p className="text-xs text-slate-500">Optimal turnaround index</p>
                    </div>
                </div>

                {/* 3. Search & Filter Bar */}
                <div className="flex flex-col sm:flex-row items-center justify-between gap-4 bg-white/85 backdrop-blur-md p-4 rounded-2xl border border-slate-200/80 shadow-xs">
                    <div className="relative w-full sm:max-w-md">
                        <Search className="absolute left-3.5 top-1/2 -translate-y-1/2 w-4 h-4 text-slate-400" />
                        <input
                            type="text"
                            placeholder="Search by client, engagement ID, or custodian..."
                            value={searchQuery}
                            onChange={(e) => setSearchQuery(e.target.value)}
                            className="w-full pl-10 pr-4 py-2 rounded-xl border border-slate-200 bg-white text-sm text-slate-800 placeholder:text-slate-400 focus:outline-none focus:ring-2 focus:ring-indigo-500/20 focus:border-indigo-500 transition"
                        />
                    </div>

                    <div className="flex items-center gap-1.5 w-full sm:w-auto">
                        {(['All', 'Started', 'Closed'] as const).map((tab) => (
                            <button
                                key={tab}
                                onClick={() => setStatusFilter(tab)}
                                className={`px-4 py-2 rounded-xl text-xs font-semibold transition ${
                                    statusFilter === tab
                                        ? 'bg-indigo-50 text-indigo-700 border border-indigo-200'
                                        : 'text-slate-600 hover:bg-slate-50 border border-transparent'
                                }`}
                            >
                                {tab === 'All' ? 'All Engagements' : tab}
                            </button>
                        ))}
                    </div>
                </div>

                {/* 4. Engagements Cards Grid */}
                {isLoadingData ? (
                    <div className="py-16 text-center space-y-3">
                        <Loader2 className="w-8 h-8 animate-spin text-indigo-600 mx-auto" />
                        <p className="text-sm text-slate-500">Loading workspace telemetry...</p>
                    </div>
                ) : filteredWorkspaces.length === 0 ? (
                    <div className="bg-white/85 backdrop-blur-md p-12 rounded-2xl border border-dashed border-slate-300 text-center space-y-4">
                        <div className="w-12 h-12 rounded-full bg-indigo-50 text-indigo-600 flex items-center justify-center mx-auto">
                            <Briefcase className="w-6 h-6" />
                        </div>
                        <div>
                            <h3 className="text-base font-bold text-slate-900">No Engagements Found</h3>
                            <p className="text-sm text-slate-500 max-w-sm mx-auto mt-1">
                                {searchQuery ? 'No client workspaces match your search term.' : 'Create an operational engagement to start the 5-stage onboarding flow.'}
                            </p>
                        </div>
                        <button
                            type="button"
                            onClick={() => setIsCreateModalOpen(true)}
                            className="px-4 py-2 rounded-xl bg-gradient-to-r from-[#635bff] to-[#712ae2] text-white text-xs font-semibold hover:opacity-95 transition inline-flex items-center gap-1.5"
                        >
                            <Plus className="w-4 h-4" />
                            <span>Create First Engagement</span>
                        </button>
                    </div>
                ) : (
                    <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-6">
                        {filteredWorkspaces.map((ws) => (
                            <div
                                key={ws.id}
                                onClick={() => handleOpenWorkspaceModal(ws)}
                                className="bg-white/90 backdrop-blur-md rounded-2xl border border-slate-200/90 hover:border-indigo-300 hover:shadow-lg hover:shadow-indigo-500/5 transition cursor-pointer p-5 flex flex-col justify-between space-y-4 group"
                            >
                                {/* Card Header */}
                                <div className="space-y-3">
                                    <div className="flex items-start justify-between gap-3">
                                        <div className="flex items-center gap-3">
                                            <div className="w-11 h-11 rounded-xl bg-gradient-to-br from-[#635bff] to-[#712ae2] text-white font-bold text-sm flex items-center justify-center shadow-sm group-hover:scale-105 transition">
                                                {ws.initials}
                                            </div>
                                            <div>
                                                <h3 className="text-base font-bold text-slate-900 group-hover:text-indigo-600 transition truncate max-w-[170px]">
                                                    {ws.clientName}
                                                </h3>
                                                <span className="text-xs font-mono font-medium text-slate-400">
                                                    {ws.code}
                                                </span>
                                            </div>
                                        </div>

                                        <span className="inline-flex items-center gap-1 px-2.5 py-0.5 rounded-full text-xs font-semibold bg-emerald-50 text-emerald-700 border border-emerald-200 shrink-0">
                                            <span className="w-1.5 h-1.5 rounded-full bg-emerald-500" />
                                            {ws.status}
                                        </span>
                                    </div>

                                    <p className="text-xs text-slate-500 line-clamp-2">
                                        {ws.title}
                                    </p>
                                </div>

                                {/* Card Body: Stage Progress */}
                                <div className="space-y-2.5 p-3 rounded-xl bg-slate-50/80 border border-slate-100">
                                    <div className="flex items-center justify-between text-xs">
                                        <span className="font-semibold text-slate-600">{ws.stageName}</span>
                                        <span className="font-bold text-indigo-600">Stage {ws.stageOrder} of 5</span>
                                    </div>

                                    {/* 5-segment mini progress bar, filled up to the engagement's real current stage */}
                                    <div className="grid grid-cols-5 gap-1">
                                        {[1, 2, 3, 4, 5].map((segment) => (
                                            <div
                                                key={segment}
                                                className={`h-1.5 rounded-full ${segment <= ws.stageOrder ? 'bg-indigo-600' : 'bg-slate-200'}`}
                                            />
                                        ))}
                                    </div>

                                    <div className="flex items-center justify-between text-[11px] text-slate-500 pt-1">
                                        <div className="flex items-center gap-1">
                                            <User className="w-3.5 h-3.5 text-slate-400" />
                                            <span className="truncate max-w-[110px]">{ws.custodians}</span>
                                        </div>
                                        <div className="flex items-center gap-1">
                                            <Calendar className="w-3.5 h-3.5 text-slate-400" />
                                            <span>{ws.createdAt}</span>
                                        </div>
                                    </div>
                                </div>

                                {/* Card Footer: Button */}
                                <div className="pt-2 flex items-center justify-between">
                                    <span className="inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-[10px] font-bold bg-indigo-50 text-indigo-700 border border-indigo-100">
                                        {ws.nextAction}
                                    </span>

                                    <button
                                        type="button"
                                        className="inline-flex items-center gap-1 text-xs font-bold text-indigo-600 group-hover:text-indigo-700 transition"
                                    >
                                        <span>Open Workspace</span>
                                        <ArrowRight className="w-3.5 h-3.5 group-hover:translate-x-0.5 transition" />
                                    </button>
                                </div>
                            </div>
                        ))}
                    </div>
                )}
            </div>

            {/* Modal 1: Engagement Inspection Modal */}
            <EngagementInspectionModal
                isOpen={!!inspectedEngagement}
                onClose={() => setInspectedEngagement(null)}
                engagement={inspectedEngagement}
                client={inspectedEngagement ? clients.find((c) => c.id === inspectedEngagement.clientId) : undefined}
                staffMember={inspectedEngagement ? teamMembers.find((m) => m.id === inspectedEngagement.staffId) : undefined}
                onProceedToWorkspace={(engId) => {
                    setInspectedEngagement(null);
                    navigate(`/workspace/${engId}`);
                }}
                onDeleted={() => {
                    setInspectedEngagement(null);
                    loadAllData();
                }}
            />

            {/* Modal 2: Team Management Modal */}
            <TeamManagementModal
                isOpen={isTeamModalOpen}
                onClose={() => setIsTeamModalOpen(false)}
            />

            {/* Modal 3: Provision New Workspace Modal */}
            {isCreateModalOpen && (
                <div className="fixed inset-0 z-50 flex items-center justify-center p-4 bg-slate-900/50 backdrop-blur-xs">
                    <div className="fixed inset-0" onClick={() => !isCreatingEngagement && setIsCreateModalOpen(false)} />
                    <div className="relative z-10 bg-white rounded-2xl shadow-xl border border-slate-200 max-w-lg w-full p-6 space-y-4">
                        <div className="flex items-center justify-between pb-3 border-b border-slate-100">
                            <div>
                                <span className="text-[10px] font-bold tracking-wider uppercase text-indigo-600 bg-indigo-50 px-2.5 py-0.5 rounded-full">
                                    PROVISION PROTOCOL
                                </span>
                                <h2 className="text-xl font-bold text-slate-900 mt-1">
                                    New Client Engagement
                                </h2>
                            </div>
                            <button
                                onClick={() => !isCreatingEngagement && setIsCreateModalOpen(false)}
                                className="p-1 rounded-lg text-slate-400 hover:text-slate-700 transition"
                            >
                                <X className="w-5 h-5" />
                            </button>
                        </div>

                        {engagementError && (
                            <div className="p-3 bg-red-50 border border-red-200 rounded-xl text-xs text-red-700">
                                {engagementError}
                            </div>
                        )}

                        {engagementSuccess && (
                            <div className="p-3 bg-emerald-50 border border-emerald-200 rounded-xl text-xs text-emerald-800 flex items-center gap-2">
                                <CheckCircle className="w-4 h-4 text-emerald-600" />
                                <span>{engagementSuccess}</span>
                            </div>
                        )}

                        <form onSubmit={handleCreateEngagement} className="space-y-4">
                            {/* Client Selection */}
                            <div>
                                <div className="flex items-center justify-between mb-1.5">
                                    <label className="text-xs font-bold text-slate-700 uppercase tracking-wider">
                                        TARGET CLIENT RECORD
                                    </label>
                                    <button
                                        type="button"
                                        onClick={() => setShowNewClientForm(!showNewClientForm)}
                                        className="text-xs text-indigo-600 font-semibold hover:underline"
                                    >
                                        {showNewClientForm ? 'Cancel' : '+ Add New Client'}
                                    </button>
                                </div>

                                {!showNewClientForm ? (
                                    <select
                                        value={selectedClientId}
                                        onChange={(e) => setSelectedClientId(e.target.value)}
                                        className="w-full px-3.5 py-2.5 rounded-xl border border-slate-200 text-sm text-slate-800 focus:outline-none focus:ring-2 focus:ring-indigo-500/20 focus:border-indigo-500"
                                        required
                                        disabled={isCreatingEngagement}
                                    >
                                        <option value="">-- Choose Existing Client --</option>
                                        {clients.map((c) => (
                                            <option key={c.id} value={c.id}>
                                                {c.name} ({c.email})
                                            </option>
                                        ))}
                                    </select>
                                ) : (
                                    <div className="p-3.5 bg-slate-50 rounded-xl space-y-2.5 border border-slate-200">
                                        <div className="text-xs font-bold text-slate-800 uppercase">
                                            Register Client in Identity Service
                                        </div>
                                        {clientError && (
                                            <div className="text-xs text-red-600">{clientError}</div>
                                        )}
                                        <input
                                            type="text"
                                            placeholder="Client company or contact name"
                                            value={newClientName}
                                            onChange={(e) => setNewClientName(e.target.value)}
                                            className="w-full px-3 py-2 rounded-lg border border-slate-200 text-xs text-slate-800 focus:outline-none focus:ring-2 focus:ring-indigo-500/20"
                                            disabled={isCreatingClient}
                                        />
                                        <input
                                            type="email"
                                            placeholder="client@company.com"
                                            value={newClientEmail}
                                            onChange={(e) => setNewClientEmail(e.target.value)}
                                            className="w-full px-3 py-2 rounded-lg border border-slate-200 text-xs text-slate-800 focus:outline-none focus:ring-2 focus:ring-indigo-500/20"
                                            disabled={isCreatingClient}
                                        />
                                        <input
                                            type="tel"
                                            placeholder="Phone number (optional)"
                                            value={newClientPhone}
                                            onChange={(e) => setNewClientPhone(e.target.value)}
                                            className="w-full px-3 py-2 rounded-lg border border-slate-200 text-xs text-slate-800 focus:outline-none focus:ring-2 focus:ring-indigo-500/20"
                                            disabled={isCreatingClient}
                                        />
                                        <button
                                            type="button"
                                            onClick={handleCreateClient}
                                            disabled={isCreatingClient}
                                            className="w-full py-2 rounded-lg bg-indigo-600 hover:bg-indigo-700 text-white text-xs font-semibold transition"
                                        >
                                            {isCreatingClient ? 'Saving Client...' : 'Save & Select Client'}
                                        </button>
                                    </div>
                                )}
                            </div>

                            {/* Staff Lead selection */}
                            <div>
                                <label className="text-xs font-bold text-slate-700 uppercase tracking-wider block mb-1.5">
                                    ASSIGNED STAFF CUSTODIAN
                                </label>
                                <select
                                    value={selectedStaffId || userId || ''}
                                    onChange={(e) => setSelectedStaffId(e.target.value)}
                                    className="w-full px-3.5 py-2.5 rounded-xl border border-slate-200 text-sm text-slate-800 focus:outline-none focus:ring-2 focus:ring-indigo-500/20 focus:border-indigo-500"
                                    disabled={isCreatingEngagement}
                                >
                                    <option value={userId || ''}>Me (Lead Custodian)</option>
                                    {teamMembers.map((m) => (
                                        <option key={m.id} value={m.id}>
                                            {m.email}
                                        </option>
                                    ))}
                                </select>
                            </div>

                            <div className="flex items-center justify-end gap-2.5 pt-4 border-t border-slate-100">
                                <button
                                    type="button"
                                    onClick={() => setIsCreateModalOpen(false)}
                                    className="px-4 py-2 rounded-xl border border-slate-200 text-slate-700 hover:bg-slate-50 text-xs font-semibold transition"
                                    disabled={isCreatingEngagement}
                                >
                                    Cancel
                                </button>
                                <button
                                    type="submit"
                                    disabled={isCreatingEngagement || !selectedClientId}
                                    className="px-5 py-2 rounded-xl bg-indigo-600 hover:bg-indigo-700 text-white text-xs font-bold transition shadow-sm"
                                >
                                    {isCreatingEngagement ? (
                                        <>
                                            <Loader2 className="w-3.5 h-3.5 animate-spin mr-1 inline" />
                                            <span>INITIALIZING...</span>
                                        </>
                                    ) : (
                                        <span>INITIALIZE WORKSPACE</span>
                                    )}
                                </button>
                            </div>
                        </form>
                    </div>
                </div>
            )}
        </DashboardLayout>
    );
};
