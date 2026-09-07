import React from 'react';
import { Engagement, ClientProfile, UserAccountResponse } from '../types';
import {
    X,
    Building2,
    Mail,
    Phone,
    Calendar,
    ArrowRight,
    Shield,
    Layers,
    User,
    CheckCircle2
} from 'lucide-react';

interface EngagementInspectionModalProps {
    isOpen: boolean;
    onClose: () => void;
    engagement: Engagement | null;
    client?: ClientProfile;
    staffMember?: UserAccountResponse;
    onProceedToWorkspace: (engagementId: string) => void;
}

export const EngagementInspectionModal: React.FC<EngagementInspectionModalProps> = ({
    isOpen,
    onClose,
    engagement,
    client,
    staffMember,
    onProceedToWorkspace,
}) => {
    if (!isOpen || !engagement) return null;

    const clientName = client?.name || `Client ${engagement.clientId.slice(0, 8)}`;
    const initials = clientName
        .split(' ')
        .map((n) => n[0])
        .join('')
        .slice(0, 2)
        .toUpperCase();

    const engagementCode = `ENG-${engagement.engagementId.slice(0, 6).toUpperCase()}`;

    const stages = [
        { num: 1, title: 'Intake & Onboarding', status: 'Active' },
        { num: 2, title: 'Compliance Evidence', status: 'Upcoming' },
        { num: 3, title: 'Gate Evaluation', status: 'Upcoming' },
        { num: 4, title: 'SLA Radar', status: 'Upcoming' },
        { num: 5, title: 'Readiness & Closure', status: 'Upcoming' },
    ];

    const currentStageIndex = engagement.status === 'Closed' ? 4 : 0;

    return (
        <div className="fixed inset-0 z-50 flex items-center justify-center p-4 bg-slate-950/40 backdrop-blur-sm">
            <div className="fixed inset-0" onClick={onClose} />
            <div className="relative z-10 bg-white/95 backdrop-blur-xl rounded-2xl shadow-2xl border border-slate-200/90 max-w-2xl w-full p-6 space-y-6">
                {/* Header */}
                <div className="flex items-start justify-between pb-4 border-b border-slate-100">
                    <div className="flex items-center gap-4">
                        <div className="w-12 h-12 rounded-xl bg-gradient-to-br from-[#635bff] to-[#712ae2] text-white font-bold text-lg flex items-center justify-center shadow-md shadow-indigo-500/20">
                            {initials}
                        </div>
                        <div>
                            <div className="flex items-center gap-2.5">
                                <h2 className="text-xl font-bold text-slate-900">
                                    {clientName}
                                </h2>
                                <span className="px-2 py-0.5 rounded text-xs font-mono font-semibold bg-slate-100 text-slate-700 border border-slate-200">
                                    {engagementCode}
                                </span>
                            </div>
                            <p className="text-xs text-slate-500 mt-0.5">
                                Isolated Client Workspace Environment • Scoped Tenant Pipeline
                            </p>
                        </div>
                    </div>
                    <button
                        onClick={onClose}
                        className="p-2 rounded-lg text-slate-400 hover:text-slate-700 hover:bg-slate-100 transition"
                    >
                        <X className="w-5 h-5" />
                    </button>
                </div>

                {/* Status & Next Action Row */}
                <div className="flex flex-wrap items-center justify-between gap-3 p-3.5 bg-slate-50/80 rounded-xl border border-slate-200/80">
                    <div className="flex items-center gap-2">
                        <span className="text-xs font-semibold text-slate-500 uppercase tracking-wider">
                            Pipeline Status:
                        </span>
                        <span className="inline-flex items-center gap-1.5 px-2.5 py-0.5 rounded-full text-xs font-semibold bg-emerald-50 text-emerald-700 border border-emerald-200">
                            <span className="w-1.5 h-1.5 rounded-full bg-emerald-500" />
                            {engagement.status}
                        </span>
                    </div>

                    <div className="flex items-center gap-1.5 px-3 py-1 rounded-full bg-indigo-50 border border-indigo-200 text-indigo-700 text-xs font-semibold">
                        <span className="w-2 h-2 rounded-full bg-indigo-600 animate-pulse" />
                        <span>NEXT: INTAKE CHECKLIST & STAKEHOLDER SETUP</span>
                    </div>
                </div>

                {/* 5-Stage Onboarding Roadmap Stepper */}
                <div className="space-y-2.5">
                    <div className="flex items-center justify-between text-xs">
                        <span className="font-bold text-slate-700 uppercase tracking-wider flex items-center gap-1.5">
                            <Layers className="w-4 h-4 text-indigo-600" />
                            5-Stage Onboarding Progression
                        </span>
                        <span className="font-semibold text-indigo-600">
                            Stage {currentStageIndex + 1} of 5 Active
                        </span>
                    </div>

                    <div className="grid grid-cols-1 sm:grid-cols-5 gap-2">
                        {stages.map((st, idx) => {
                            const isCurrent = idx === currentStageIndex;
                            const isPassed = idx < currentStageIndex;
                            return (
                                <div
                                    key={st.num}
                                    className={`p-3 rounded-xl border text-center transition ${
                                        isCurrent
                                            ? 'bg-indigo-50/70 border-indigo-300 ring-2 ring-indigo-500/20 shadow-xs'
                                            : isPassed
                                            ? 'bg-emerald-50 border-emerald-200 text-emerald-800'
                                            : 'bg-slate-50 border-slate-200 text-slate-500'
                                    }`}
                                >
                                    <div
                                        className={`w-5 h-5 rounded-full mx-auto mb-1.5 flex items-center justify-center text-[10px] font-bold ${
                                            isCurrent
                                                ? 'bg-indigo-600 text-white'
                                                : isPassed
                                                ? 'bg-emerald-600 text-white'
                                                : 'bg-slate-200 text-slate-600'
                                        }`}
                                    >
                                        {isPassed ? '✓' : st.num}
                                    </div>
                                    <div className="text-xs font-semibold text-slate-800 truncate">
                                        {st.title}
                                    </div>
                                    <div className="text-[10px] text-slate-500 mt-0.5">
                                        {isCurrent ? 'Active Stage' : isPassed ? 'Completed' : 'Upcoming'}
                                    </div>
                                </div>
                            );
                        })}
                    </div>
                </div>

                {/* Details Grid */}
                <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
                    {/* Assigned Custodian */}
                    <div className="p-3.5 bg-slate-50/80 rounded-xl border border-slate-200/80 space-y-1.5">
                        <span className="text-[11px] font-bold text-slate-500 uppercase tracking-wider block">
                            Assigned Custodian Lead
                        </span>
                        <div className="flex items-center gap-2.5">
                            <div className="w-8 h-8 rounded-full bg-indigo-100 text-indigo-700 font-bold flex items-center justify-center text-xs">
                                <User className="w-4 h-4" />
                            </div>
                            <div className="overflow-hidden">
                                <p className="text-xs font-bold text-slate-800 truncate">
                                    {staffMember?.email || 'Assigned Staff Custodian'}
                                </p>
                                <p className="text-[11px] text-slate-500 font-mono truncate">
                                    Staff ID: {engagement.staffId.slice(0, 12)}...
                                </p>
                            </div>
                        </div>
                    </div>

                    {/* Client Record Details */}
                    <div className="p-3.5 bg-slate-50/80 rounded-xl border border-slate-200/80 space-y-1.5">
                        <span className="text-[11px] font-bold text-slate-500 uppercase tracking-wider block">
                            Target Client Record
                        </span>
                        <div className="space-y-1 text-xs text-slate-600">
                            <div className="flex items-center gap-1.5">
                                <Mail className="w-3.5 h-3.5 text-slate-400" />
                                <span className="truncate">{client?.email || 'client@organization.com'}</span>
                            </div>
                            {client?.phone && (
                                <div className="flex items-center gap-1.5">
                                    <Phone className="w-3.5 h-3.5 text-slate-400" />
                                    <span>{client.phone}</span>
                                </div>
                            )}
                        </div>
                    </div>
                </div>

                {/* Modal Footer Actions */}
                <div className="flex items-center justify-end gap-3 pt-4 border-t border-slate-100">
                    <button
                        type="button"
                        onClick={onClose}
                        className="px-4 py-2.5 rounded-xl border border-slate-200 text-slate-700 hover:bg-slate-50 text-xs font-semibold transition"
                    >
                        Cancel
                    </button>
                    <button
                        type="button"
                        onClick={() => {
                            onProceedToWorkspace(engagement.engagementId);
                            onClose();
                        }}
                        className="px-5 py-2.5 rounded-xl bg-gradient-to-r from-[#635bff] to-[#712ae2] hover:opacity-95 text-white text-xs font-bold flex items-center gap-2 shadow-sm shadow-indigo-500/25 transition"
                    >
                        <span>Proceed into Workspace</span>
                        <ArrowRight className="w-4 h-4" />
                    </button>
                </div>
            </div>
        </div>
    );
};
