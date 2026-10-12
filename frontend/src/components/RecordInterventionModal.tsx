import React, { useState } from 'react';
import { X, Loader2, CheckCircle, AlertTriangle, Loader2 as _ } from 'lucide-react';
import { WorkflowApi, ApiError } from '../services/api';
import { RecordInterventionRequest } from '../types';

interface RecordInterventionModalProps {
    isOpen: boolean;
    onClose: () => void;
    onRecorded: () => void;
    tenantId: string;
    engagementId: string;
    engagementLabel: string;
    blockerActionId?: string | null;
    blockerActionTitle?: string | null;
    meetingId?: string | null;
    stallId?: string | null;
}

const TYPES = ['RecoveryAction', 'Meeting'] as const;
const OUTCOMES = ['Progressing', 'Recovered', 'NoChange', 'Escalated'] as const;

export const RecordInterventionModal: React.FC<RecordInterventionModalProps> = ({
    isOpen,
    onClose,
    onRecorded,
    tenantId,
    engagementId,
    engagementLabel,
    blockerActionId,
    blockerActionTitle,
    meetingId,
    stallId,
}) => {
    const [type, setType] = useState<(typeof TYPES)[number]>('RecoveryAction');
    const [outcome, setOutcome] = useState<(typeof OUTCOMES)[number]>('Progressing');
    const [reason, setReason] = useState('');
    const [isSubmitting, setIsSubmitting] = useState(false);
    const [error, setError] = useState<string | null>(null);

    if (!isOpen) return null;

    const reset = () => {
        setType('RecoveryAction');
        setOutcome('Progressing');
        setReason('');
        setError(null);
        setIsSubmitting(false);
    };

    const handleClose = () => {
        if (isSubmitting) return;
        reset();
        onClose();
    };

    const handleSubmit = async (e: React.FormEvent) => {
        e.preventDefault();
        setError(null);

        const trimmed = reason.trim();
        if (!trimmed) {
            setError('Reason is required.');
            return;
        }
        if (trimmed.length > 2000) {
            setError('Reason must be 2000 characters or fewer.');
            return;
        }

        setIsSubmitting(true);
        try {
            const payload: RecordInterventionRequest = {
                type,
                outcome,
                reason: trimmed,
                blockerActionId: blockerActionId ?? null,
                stallId: stallId ?? null,
                meetingId: type === 'Meeting' ? (meetingId ?? null) : null,
            };

            await WorkflowApi.recordIntervention(engagementId, payload, tenantId);

            reset();
            onRecorded();
            onClose();
        } catch (err: any) {
            if (err instanceof ApiError) setError(err.message);
            else setError(err?.message || 'Failed to record intervention.');
        } finally {
            setIsSubmitting(false);
        }
    };

    return (
        <div className="fixed inset-0 z-50 flex items-center justify-center p-4 bg-slate-900/50 backdrop-blur-xs">
            <div className="fixed inset-0" onClick={handleClose} />
            <div className="relative z-10 bg-white rounded-2xl shadow-xl border border-slate-200 max-w-lg w-full p-6 space-y-4">
                <div className="flex items-center justify-between pb-3 border-b border-slate-100">
                    <div>
                        <span className="text-[10px] font-bold tracking-wider uppercase text-amber-700 bg-amber-50 px-2.5 py-0.5 rounded-full">
                            RECOVERY ACTION
                        </span>
                        <h2 className="text-xl font-bold text-slate-900 mt-1">Record Intervention</h2>
                        <p className="text-xs text-slate-500 mt-0.5 truncate max-w-[380px]">
                            {engagementLabel}
                        </p>
                    </div>
                    <button
                        onClick={handleClose}
                        disabled={isSubmitting}
                        className="p-1 rounded-lg text-slate-400 hover:text-slate-700 transition disabled:opacity-50"
                    >
                        <X className="w-5 h-5" />
                    </button>
                </div>

                {blockerActionTitle && (
                    <div className="p-2.5 rounded-xl bg-slate-50 border border-slate-200 text-xs text-slate-700">
                        <span className="font-semibold">Blocker:</span> {blockerActionTitle}
                    </div>
                )}

                {error && (
                    <div className="p-3 bg-red-50 border border-red-200 rounded-xl text-xs text-red-700 flex items-start gap-2">
                        <AlertTriangle className="w-4 h-4 shrink-0 mt-0.5" />
                        <span>{error}</span>
                    </div>
                )}

                <form onSubmit={handleSubmit} className="space-y-4">
                    <div className="grid grid-cols-2 gap-3">
                        <div>
                            <label className="text-[11px] font-bold text-slate-700 uppercase tracking-wider block mb-1.5">
                                Type
                            </label>
                            <select
                                value={type}
                                onChange={(e) => setType(e.target.value as (typeof TYPES)[number])}
                                disabled={isSubmitting}
                                className="w-full px-3 py-2 rounded-xl border border-slate-200 text-sm text-slate-800 focus:outline-none focus:ring-2 focus:ring-indigo-500/20 focus:border-indigo-500"
                            >
                                {TYPES.map((t) => (
                                    <option key={t} value={t}>
                                        {t === 'RecoveryAction' ? 'Recovery Action' : 'Meeting'}
                                    </option>
                                ))}
                            </select>
                        </div>
                        <div>
                            <label className="text-[11px] font-bold text-slate-700 uppercase tracking-wider block mb-1.5">
                                Outcome
                            </label>
                            <select
                                value={outcome}
                                onChange={(e) => setOutcome(e.target.value as (typeof OUTCOMES)[number])}
                                disabled={isSubmitting}
                                className="w-full px-3 py-2 rounded-xl border border-slate-200 text-sm text-slate-800 focus:outline-none focus:ring-2 focus:ring-indigo-500/20 focus:border-indigo-500"
                            >
                                {OUTCOMES.map((o) => (
                                    <option key={o} value={o}>{o}</option>
                                ))}
                            </select>
                        </div>
                    </div>

                    <div>
                        <label className="text-[11px] font-bold text-slate-700 uppercase tracking-wider block mb-1.5">
                            Reason
                        </label>
                        <textarea
                            value={reason}
                            onChange={(e) => setReason(e.target.value)}
                            disabled={isSubmitting}
                            rows={4}
                            maxLength={2000}
                            placeholder="What did you do to unblock this engagement? Include any commitment from the client or next step."
                            className="w-full px-3 py-2 rounded-xl border border-slate-200 text-sm text-slate-800 focus:outline-none focus:ring-2 focus:ring-indigo-500/20 focus:border-indigo-500 resize-none"
                        />
                        <div className="text-[10px] text-slate-400 mt-1 text-right">
                            {reason.length} / 2000
                        </div>
                    </div>

                    <div className="p-2.5 rounded-xl bg-indigo-50/60 border border-indigo-100 text-[11px] text-indigo-900 leading-relaxed">
                        Positive outcomes (<strong>Recovered</strong>, <strong>Progressing</strong>) send
                        a client-safe notification. <strong>NoChange</strong> and <strong>Escalated</strong>
                        {' '}are recorded for internal history only.
                    </div>

                    <div className="flex items-center justify-end gap-2.5 pt-2 border-t border-slate-100">
                        <button
                            type="button"
                            onClick={handleClose}
                            disabled={isSubmitting}
                            className="px-4 py-2 rounded-xl border border-slate-200 text-slate-700 hover:bg-slate-50 text-xs font-semibold transition disabled:opacity-50"
                        >
                            Cancel
                        </button>
                        <button
                            type="submit"
                            disabled={isSubmitting || !reason.trim()}
                            className="px-5 py-2 rounded-xl bg-indigo-600 hover:bg-indigo-700 text-white text-xs font-bold transition shadow-sm disabled:opacity-50 inline-flex items-center gap-1.5"
                        >
                            {isSubmitting ? (
                                <>
                                    <Loader2 className="w-3.5 h-3.5 animate-spin" />
                                    <span>Recording...</span>
                                </>
                            ) : (
                                <span>Record Intervention</span>
                            )}
                        </button>
                    </div>
                </form>
            </div>
        </div>
    );
};

export default RecordInterventionModal;