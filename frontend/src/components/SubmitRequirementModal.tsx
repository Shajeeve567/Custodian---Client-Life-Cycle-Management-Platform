import React, { useState } from 'react';
import { ClientSafeAction } from '../types';
import { WorkflowApi } from '../services/api';
import { X, ClipboardList, AlertCircle, CheckCircle2, Loader2 } from 'lucide-react';

interface SubmitRequirementModalProps {
    isOpen: boolean;
    onClose: () => void;
    action: ClientSafeAction | null;
    engagementId: string;
    tenantId: string;
    userId?: string | null;
    onSuccess: () => void;
}

/**
 * CSTD-16 (Requirements Collection). Submits the client's answer for a Requirement-backed
 * ClientAction (Type: "Requirement", identified by action.linkedRequirementId) via
 * PUT /requirements/{id}/submit — deliberately NOT the generic complete/upload endpoints,
 * which don't know how to set the underlying Requirement's Value/Status.
 */
export const SubmitRequirementModal: React.FC<SubmitRequirementModalProps> = ({
    isOpen,
    onClose,
    action,
    engagementId,
    tenantId,
    userId,
    onSuccess,
}) => {
    const [value, setValue] = useState<string>('');
    const [isSubmitting, setIsSubmitting] = useState<boolean>(false);
    const [error, setError] = useState<string | null>(null);

    if (!isOpen || !action) return null;

    const handleResetAndClose = () => {
        setValue('');
        setError(null);
        onClose();
    };

    const handleSubmit = async (e: React.FormEvent) => {
        e.preventDefault();
        if (!action.linkedRequirementId || !engagementId || !tenantId) return;

        if (!value.trim()) {
            setError('Please provide a value before submitting.');
            return;
        }

        setIsSubmitting(true);
        setError(null);

        try {
            await WorkflowApi.submitRequirement(
                engagementId,
                action.linkedRequirementId,
                {
                    value: value.trim(),
                    submittedByActor: userId || 'client-user',
                },
                tenantId
            );

            onSuccess();
            handleResetAndClose();
        } catch (err: any) {
            setError(err.message || 'Failed to submit. Please try again.');
        } finally {
            setIsSubmitting(false);
        }
    };

    return (
        <div className="fixed inset-0 z-50 flex items-center justify-center p-4 bg-slate-900/60 backdrop-blur-xs animate-in fade-in duration-200">
            <div className="bg-white rounded-2xl shadow-xl border border-slate-200 w-full max-w-lg overflow-hidden">
                {/* Header */}
                <div className="p-5 border-b border-slate-100 flex items-center justify-between">
                    <div className="flex items-center gap-3">
                        <div className="w-10 h-10 rounded-xl bg-indigo-50 border border-indigo-100 flex items-center justify-center text-indigo-600">
                            <ClipboardList className="w-5 h-5" />
                        </div>
                        <div>
                            <h3 className="text-base font-bold text-slate-900">Provide Requested Information</h3>
                            <p className="text-xs text-slate-500">
                                Stage {action.stageNumber} • {action.title}
                            </p>
                        </div>
                    </div>
                    <button
                        onClick={handleResetAndClose}
                        disabled={isSubmitting}
                        className="p-1.5 rounded-lg text-slate-400 hover:text-slate-600 hover:bg-slate-100 transition"
                    >
                        <X className="w-5 h-5" />
                    </button>
                </div>

                {/* Form Body */}
                <form onSubmit={handleSubmit} className="p-6 space-y-4">
                    {action.description && (
                        <p className="text-xs text-slate-600 leading-relaxed">{action.description}</p>
                    )}

                    {error && (
                        <div className="p-3 bg-rose-50 border border-rose-200 rounded-xl text-rose-800 text-xs flex items-start gap-2">
                            <AlertCircle className="w-4 h-4 text-rose-600 shrink-0 mt-0.5" />
                            <span>{error}</span>
                        </div>
                    )}

                    {action.rejectionReason && (
                        <div className="p-3 bg-amber-50 border border-amber-200 rounded-xl text-amber-800 text-xs flex items-start gap-2">
                            <AlertCircle className="w-4 h-4 text-amber-600 shrink-0 mt-0.5" />
                            <span>Previously rejected: {action.rejectionReason}</span>
                        </div>
                    )}

                    <div>
                        <label className="block text-xs font-semibold text-slate-700 mb-1">
                            Your Response <span className="text-rose-500">*</span>
                        </label>
                        <textarea
                            rows={4}
                            value={value}
                            onChange={(e) => setValue(e.target.value)}
                            placeholder="Enter the requested information..."
                            className="w-full px-3 py-2 text-xs rounded-xl border border-slate-200 focus:outline-none focus:ring-2 focus:ring-indigo-500/20 focus:border-indigo-500 text-slate-800"
                        />
                    </div>

                    {/* Modal Footer */}
                    <div className="pt-2 flex items-center justify-end gap-2.5">
                        <button
                            type="button"
                            onClick={handleResetAndClose}
                            disabled={isSubmitting}
                            className="px-4 py-2 rounded-xl border border-slate-200 text-xs font-semibold text-slate-600 hover:bg-slate-50 transition"
                        >
                            Cancel
                        </button>
                        <button
                            type="submit"
                            disabled={!value.trim() || isSubmitting}
                            className={`px-5 py-2 rounded-xl text-xs font-bold text-white flex items-center gap-2 transition shadow-sm ${
                                value.trim() && !isSubmitting
                                    ? 'bg-gradient-to-r from-[#635bff] to-[#712ae2] hover:opacity-95 shadow-indigo-500/25 cursor-pointer'
                                    : 'bg-slate-200 text-slate-400 cursor-not-allowed'
                            }`}
                        >
                            {isSubmitting ? (
                                <>
                                    <Loader2 className="w-4 h-4 animate-spin" />
                                    <span>Submitting...</span>
                                </>
                            ) : (
                                <>
                                    <CheckCircle2 className="w-4 h-4" />
                                    <span>Submit</span>
                                </>
                            )}
                        </button>
                    </div>
                </form>
            </div>
        </div>
    );
};
