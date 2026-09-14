import React, { useState } from 'react';
import { ClientSafeAction } from '../types';
import { ApiError, WorkflowApi } from '../services/api';
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

// CSTD-22 (Guided Requirement Submission): a real minimum catches accidental single-character
// submissions without being so strict it rejects a genuinely short but valid answer (e.g. "USD").
// The maximum matches nothing on the backend specifically (Requirement.Value is `longtext`) —
// it's a sane UI guard against pasting something far larger than "a piece of information" this
// form is meant for, checked client-side so the user finds out before waiting on a round-trip.
const MIN_VALUE_LENGTH = 2;
const MAX_VALUE_LENGTH = 2000;

/**
 * Maps a submission failure to a human-readable message per CSTD-22's "validation is
 * human-readable" AC. ASP.NET's ModelState errors (400) already arrive pre-joined into a
 * readable string via api.ts's request() helper (e.g. "The Value field is required.") — passed
 * through as-is. Other status codes get a message ASP.NET's bare Forbid()/NotFound() wouldn't
 * otherwise produce (an empty-bodied 403, for instance, would otherwise surface as "Forbidden").
 */
function describeSubmitError(err: unknown): string {
    if (err instanceof ApiError) {
        switch (err.status) {
            case 400:
                return err.message || 'Please check your response and try again.';
            case 403:
                return "You don't have permission to submit this. Please refresh the page and try again.";
            case 404:
                return 'This request could not be found — it may have already been handled. Please refresh and try again.';
            default:
                if (err.status >= 500) {
                    return "Something went wrong on our end submitting your response. Please try again in a moment.";
                }
                return err.message || 'Failed to submit. Please try again.';
        }
    }
    if (err instanceof Error && err.message) {
        return err.message;
    }
    return 'Failed to submit. Please try again.';
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
    // Field-level: shown right under the textarea, cleared as soon as the user edits it again.
    const [fieldError, setFieldError] = useState<string | null>(null);
    // Request-level: shown as a banner — network/server failures the field itself can't describe.
    const [submitError, setSubmitError] = useState<string | null>(null);

    if (!isOpen || !action) return null;

    const handleResetAndClose = () => {
        setValue('');
        setFieldError(null);
        setSubmitError(null);
        onClose();
    };

    /** Returns a human-readable message if `value` isn't submittable, or null if it's fine. */
    const validate = (candidate: string): string | null => {
        const trimmed = candidate.trim();
        if (!trimmed) {
            return 'This field is required.';
        }
        if (trimmed.length < MIN_VALUE_LENGTH) {
            return `Please provide a more complete response (at least ${MIN_VALUE_LENGTH} characters).`;
        }
        if (trimmed.length > MAX_VALUE_LENGTH) {
            return `Response is too long — please keep it under ${MAX_VALUE_LENGTH} characters (currently ${trimmed.length}).`;
        }
        return null;
    };

    const handleValueChange = (next: string) => {
        setValue(next);
        // Re-validate live once the user has already tried to submit once, so the error clears
        // the moment they fix it rather than lingering until the next submit attempt.
        if (fieldError) {
            setFieldError(validate(next));
        }
    };

    const handleSubmit = async (e: React.FormEvent) => {
        e.preventDefault();
        if (!action.linkedRequirementId || !engagementId || !tenantId) return;

        const validationMessage = validate(value);
        if (validationMessage) {
            setFieldError(validationMessage);
            return;
        }

        setIsSubmitting(true);
        setFieldError(null);
        setSubmitError(null);

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
        } catch (err) {
            setSubmitError(describeSubmitError(err));
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

                    {submitError && (
                        <div className="p-3 bg-rose-50 border border-rose-200 rounded-xl text-rose-800 text-xs flex items-start gap-2">
                            <AlertCircle className="w-4 h-4 text-rose-600 shrink-0 mt-0.5" />
                            <span>{submitError}</span>
                        </div>
                    )}

                    {action.rejectionReason && (
                        <div className="p-3 bg-amber-50 border border-amber-200 rounded-xl text-amber-800 text-xs flex items-start gap-2">
                            <AlertCircle className="w-4 h-4 text-amber-600 shrink-0 mt-0.5" />
                            <span>Previously rejected: {action.rejectionReason}</span>
                        </div>
                    )}

                    <div>
                        <div className="flex items-center justify-between mb-1">
                            <label className="block text-xs font-semibold text-slate-700">
                                Your Response <span className="text-rose-500">*</span>
                            </label>
                            <span className={`text-[10px] ${value.length > MAX_VALUE_LENGTH ? 'text-rose-600 font-semibold' : 'text-slate-400'}`}>
                                {value.length}/{MAX_VALUE_LENGTH}
                            </span>
                        </div>
                        <textarea
                            rows={4}
                            value={value}
                            onChange={(e) => handleValueChange(e.target.value)}
                            placeholder="Enter the requested information..."
                            aria-invalid={Boolean(fieldError)}
                            className={`w-full px-3 py-2 text-xs rounded-xl border focus:outline-none focus:ring-2 text-slate-800 ${
                                fieldError
                                    ? 'border-rose-300 focus:ring-rose-500/20 focus:border-rose-500'
                                    : 'border-slate-200 focus:ring-indigo-500/20 focus:border-indigo-500'
                            }`}
                        />
                        {fieldError && (
                            <p className="text-[11px] text-rose-600 font-medium mt-1">{fieldError}</p>
                        )}
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
