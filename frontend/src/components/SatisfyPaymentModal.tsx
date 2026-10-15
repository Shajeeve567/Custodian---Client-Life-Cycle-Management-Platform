import React, { useState, useEffect } from 'react';
import { createPortal } from 'react-dom';
import { WorkflowApi, ApiError } from '../services/api';
import { EngagementCondition } from '../types';
import { CheckCircle2, X, AlertCircle, AlertTriangle, Loader2 } from 'lucide-react';

export interface SatisfyPaymentModalProps {
    condition: EngagementCondition;   // the payment condition
    engagementId: string;
    tenantId: string;
    onClose: () => void;
    onSatisfied: () => void;
}

export const SatisfyPaymentModal: React.FC<SatisfyPaymentModalProps> = ({
    condition,
    engagementId,
    tenantId,
    onClose,
    onSatisfied,
}) => {
    const [isSubmitting, setIsSubmitting] = useState(false);
    const [error, setError] = useState<string | null>(null);

    useEffect(() => {
        const handleKeyDown = (e: KeyboardEvent) => {
            if (e.key === 'Escape' && !isSubmitting) {
                onClose();
            }
        };
        window.addEventListener('keydown', handleKeyDown);
        return () => window.removeEventListener('keydown', handleKeyDown);
    }, [onClose, isSubmitting]);

    const formattedAmount = condition.amount != null
        ? `${(condition.currency || 'USD').toUpperCase() === 'USD' ? '$' : ''}${condition.amount.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })} ${condition.currency || 'USD'}`
        : null;

    const handleConfirm = async () => {
        setError(null);
        setIsSubmitting(true);
        try {
            await WorkflowApi.satisfyCondition(engagementId, condition.conditionId, tenantId);
            onSatisfied();
            onClose();
        } catch (err: any) {
            setError(err instanceof ApiError ? err.message : (err?.message || 'Failed to mark the payment satisfied.'));
        } finally {
            setIsSubmitting(false);
        }
    };

    return createPortal(
        <div
            className="fixed inset-0 z-50 flex items-center justify-center p-4 bg-slate-900/60 backdrop-blur-xs"
            onClick={(e) => {
                if (e.target === e.currentTarget && !isSubmitting) onClose();
            }}
        >
            <div className="bg-white rounded-2xl max-w-md w-full p-6 shadow-2xl border border-slate-200 space-y-4 animate-in fade-in zoom-in-95 duration-150">
                {/* Header */}
                <div className="flex items-center justify-between pb-3 border-b border-slate-100">
                    <div className="flex items-center gap-2.5">
                        <div className="p-2 rounded-xl bg-emerald-50 text-emerald-600 border border-emerald-100">
                            <CheckCircle2 className="w-5 h-5" />
                        </div>
                        <div>
                            <h3 className="text-base font-bold text-slate-900">Mark payment satisfied</h3>
                            <p className="text-xs text-slate-500">Clears the gate blocking the next stage.</p>
                        </div>
                    </div>
                    <button
                        type="button"
                        onClick={onClose}
                        disabled={isSubmitting}
                        className="p-1 rounded-lg text-slate-400 hover:text-slate-600 hover:bg-slate-100 transition"
                        aria-label="Close"
                    >
                        <X className="w-5 h-5" />
                    </button>
                </div>

                {/* Amber Warning Block */}
                <div className="p-3.5 rounded-xl bg-amber-50 border border-amber-200 text-xs text-amber-900 flex items-start gap-2.5">
                    <AlertCircle className="w-4 h-4 text-amber-600 shrink-0 mt-0.5" />
                    <p className="text-xs text-amber-800 leading-relaxed">
                        The client will be notified. The gate blocker will be cleared and the engagement can advance.
                    </p>
                </div>

                {/* Slate Detail Block */}
                <div className="p-3.5 rounded-xl bg-slate-50 border border-slate-200 text-xs">
                    <div className="font-semibold text-slate-800">
                        {condition.title}
                        {formattedAmount && <span> — {formattedAmount}</span>}
                    </div>
                    {condition.description && (
                        <p className="text-[11px] text-slate-500 mt-1 leading-relaxed">
                            {condition.description}
                        </p>
                    )}
                </div>

                {/* Inline Rose Error Banner */}
                {error && (
                    <div className="p-3 bg-rose-50 border border-rose-200 rounded-xl text-rose-800 text-xs font-semibold flex items-center gap-2">
                        <AlertTriangle className="w-4 h-4 shrink-0" />
                        <span>{error}</span>
                    </div>
                )}

                {/* Action Buttons */}
                <div className="flex items-center justify-end gap-3 pt-3 border-t border-slate-100">
                    <button
                        type="button"
                        onClick={onClose}
                        disabled={isSubmitting}
                        className="px-4 py-2 text-xs font-semibold text-slate-600 hover:text-slate-800 border border-slate-200 rounded-xl hover:bg-slate-50 transition disabled:opacity-50"
                    >
                        Cancel
                    </button>
                    <button
                        type="button"
                        onClick={handleConfirm}
                        disabled={isSubmitting}
                        className="inline-flex items-center gap-2 px-5 py-2.5 rounded-xl text-xs font-bold text-white bg-emerald-600 hover:bg-emerald-700 shadow-sm transition disabled:opacity-50"
                    >
                        {isSubmitting ? (
                            <>
                                <Loader2 className="w-3.5 h-3.5 animate-spin" />
                                <span>Marking satisfied...</span>
                            </>
                        ) : (
                            <>
                                <CheckCircle2 className="w-4 h-4" />
                                <span>Mark satisfied</span>
                            </>
                        )}
                    </button>
                </div>
            </div>
        </div>,
        document.body
    );
};
