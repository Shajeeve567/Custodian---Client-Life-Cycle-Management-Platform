import React, { useEffect, useState } from 'react';
import { WorkflowApi } from '../services/api';
import { ClientAction, UpdateClientActionRequest } from '../types';
import { ENGAGEMENT_STAGES } from '../constants/engagementStages';
import { AlertTriangle, Ban, CheckSquare, Edit3, Loader2, X } from 'lucide-react';

/**
 * Staff-defined stage tasks: tasks staff own (Manual / Lifecycle source). Tasks mirroring a
 * requirement or belonging to a condition are managed through their source, never here.
 */
export const isStaffManagedTask = (action: ClientAction): boolean =>
    !action.linkedRequirementId &&
    !action.linkedConditionId &&
    action.sourceType !== 'Requirement' &&
    action.sourceType !== 'Condition';

/** Edit/Add Task form value for "Request Information" (a question the client answers). */
export const REQUEST_INFORMATION_OPTION = 'RequestInformation';

/** The plain and document task types staff can choose (Request Information is offered separately). */
export const TASK_TYPE_OPTIONS: { value: string; label: string }[] = [
    { value: 'CustomTask', label: 'Custom Task (General)' },
    { value: 'KycDocument', label: 'KYC Document' },
    { value: 'SignAgreement', label: 'Sign Agreement / Contract' },
    { value: 'ProofOfAddress', label: 'Proof of Address' },
    { value: 'DocumentUpload', label: 'Document Upload' },
];

const inputClass =
    'w-full text-xs bg-slate-50 border border-slate-200 rounded-xl p-2.5 text-slate-800 focus:outline-none focus:ring-2 focus:ring-indigo-500/20 focus:border-indigo-500';

const toDateInput = (value?: string | null) => (value ? value.slice(0, 10) : '');

const resolveRole = (action: ClientAction): 'Client' | 'Staff' =>
    (action.assignedToRole ?? action.assignedRole) === 'Staff' ? 'Staff' : 'Client';

const ModalShell: React.FC<{
    icon: React.ReactNode;
    title: string;
    subtitle: string;
    error: string | null;
    onClose: () => void;
    children: React.ReactNode;
}> = ({ icon, title, subtitle, error, onClose, children }) => (
    <div className="fixed inset-0 z-50 bg-slate-900/50 backdrop-blur-sm flex items-center justify-center p-4">
        <div className="bg-white rounded-2xl max-w-lg w-full p-6 shadow-2xl border border-slate-200 space-y-4">
            <div className="flex items-center justify-between pb-3 border-b border-slate-100">
                <div className="flex items-center gap-2.5">
                    {icon}
                    <div>
                        <h3 className="text-base font-bold text-slate-900">{title}</h3>
                        <p className="text-xs text-slate-500">{subtitle}</p>
                    </div>
                </div>
                <button type="button" onClick={onClose} className="p-1 rounded-lg text-slate-400 hover:text-slate-600 hover:bg-slate-100">
                    <X className="w-5 h-5" />
                </button>
            </div>
            {error && (
                <div className="p-3 bg-red-50 border border-red-200 rounded-xl text-red-700 text-xs font-semibold flex items-center gap-2">
                    <AlertTriangle className="w-4 h-4 shrink-0" />
                    <span>{error}</span>
                </div>
            )}
            {children}
        </div>
    </div>
);

interface EditTaskModalProps {
    action: ClientAction;
    engagementId: string;
    tenantId: string;
    /** Current stage number (1-5): tasks can only move to this stage or a later one. */
    minStage: number;
    onClose: () => void;
    onSaved: () => void | Promise<void>;
}

/** Edit a Pending, staff-managed task. Only changed fields are sent (PATCH). */
export const EditTaskModal: React.FC<EditTaskModalProps> = ({ action, engagementId, tenantId, minStage, onClose, onSaved }) => {
    const initialDeadline = toDateInput(action.deadlineUtc);
    const [title, setTitle] = useState(action.title);
    const [description, setDescription] = useState(action.description ?? '');
    const [stage, setStage] = useState<number>(action.stageNumber ?? minStage);
    const [role, setRole] = useState<'Client' | 'Staff'>(resolveRole(action));
    const [deadline, setDeadline] = useState(initialDeadline);
    // A Request Information task (question to the client) keeps its type and role; only the question,
    // description, stage and deadline can be edited, and only until the client answers.
    const isQuestion = Boolean(action.linkedRequirementId);
    const [type, setType] = useState(isQuestion ? REQUEST_INFORMATION_OPTION : action.type);
    // The type decides how the task is completed; once a document is attached it can no longer change.
    const typeLocked = isQuestion || Boolean(action.linkedDocumentId);
    const becomesQuestion = !isQuestion && type === REQUEST_INFORMATION_OPTION;
    const roleLocked = isQuestion || becomesQuestion;
    const [isSaving, setIsSaving] = useState(false);
    const [error, setError] = useState<string | null>(null);

    const handleSubmit = async (e: React.FormEvent) => {
        e.preventDefault();
        if (!title.trim()) {
            setError('Task title is required.');
            return;
        }

        const req: UpdateClientActionRequest = {};
        if (title.trim() !== action.title) req.title = title.trim();
        if (description !== (action.description ?? '')) req.description = description;
        if (stage !== action.stageNumber) req.stageNumber = stage;
        if (!isQuestion && type !== action.type) req.type = type;
        if (!roleLocked && role !== resolveRole(action)) {
            req.assignedToRole = role;
            // Same convention as task creation: staff tasks are internal-only.
            req.isInternalOnly = role === 'Staff';
        }
        if (deadline !== initialDeadline) {
            if (deadline) req.deadlineUtc = new Date(deadline).toISOString();
            else req.clearDeadline = true;
        }

        if (Object.keys(req).length === 0) {
            onClose();
            return;
        }

        setIsSaving(true);
        setError(null);
        try {
            await WorkflowApi.updateAction(engagementId, action.actionId, req, tenantId);
            await onSaved();
            onClose();
        } catch (err: any) {
            setError(err?.message || 'Failed to update task.');
        } finally {
            setIsSaving(false);
        }
    };

    return (
        <ModalShell
            icon={<div className="p-2 rounded-xl bg-indigo-50 text-indigo-600"><Edit3 className="w-5 h-5" /></div>}
            title="Edit Task"
            subtitle="Only pending tasks can be edited."
            error={error}
            onClose={onClose}
        >
            <form onSubmit={handleSubmit} className="space-y-3.5">
                <div>
                    <label className="block text-xs font-bold text-slate-700 mb-1">
                        Task Title <span className="text-rose-500">*</span>
                    </label>
                    <input type="text" required value={title} onChange={(e) => setTitle(e.target.value)} className={inputClass} />
                </div>
                <div>
                    <label className="block text-xs font-bold text-slate-700 mb-1">Description / Instructions</label>
                    <textarea rows={2} value={description} onChange={(e) => setDescription(e.target.value)} className={inputClass} />
                </div>
                <div>
                    <label className="block text-xs font-bold text-slate-700 mb-1">Task Type</label>
                    <select
                        value={type}
                        onChange={(e) => setType(e.target.value)}
                        disabled={typeLocked}
                        title={
                            isQuestion
                                ? 'A question stays a question; cancel it and add a new task to change its type.'
                                : typeLocked
                                ? "The type can't change after a document was uploaded for this task."
                                : undefined
                        }
                        className={inputClass}
                    >
                        {!isQuestion && !TASK_TYPE_OPTIONS.some((o) => o.value === action.type) && (
                            <option value={action.type}>{action.type}</option>
                        )}
                        {TASK_TYPE_OPTIONS.map((o) => (
                            <option key={o.value} value={o.value}>{o.label}</option>
                        ))}
                        <option value={REQUEST_INFORMATION_OPTION}>Request Information (client answers)</option>
                    </select>
                    {isQuestion ? (
                        <p className="text-[11px] text-slate-500 mt-1">Question to the client: editable until they answer.</p>
                    ) : typeLocked ? (
                        <p className="text-[11px] text-slate-500 mt-1">Locked: a document was already uploaded for this task.</p>
                    ) : becomesQuestion ? (
                        <p className="text-[11px] text-indigo-600 mt-1">The client will answer this in their portal; staff then approve or reject the answer.</p>
                    ) : null}
                </div>
                <div className="grid grid-cols-1 sm:grid-cols-3 gap-3">
                    <div>
                        <label className="block text-xs font-bold text-slate-700 mb-1">Stage</label>
                        <select value={stage} onChange={(e) => setStage(Number(e.target.value))} className={inputClass}>
                            {ENGAGEMENT_STAGES.map((s) => (
                                <option key={s.key} value={s.order} disabled={s.order < minStage}>
                                    Stage {s.order}: {s.name}
                                </option>
                            ))}
                        </select>
                    </div>
                    <div>
                        <label className="block text-xs font-bold text-slate-700 mb-1">Assigned Role</label>
                        <select
                            value={roleLocked ? 'Client' : role}
                            disabled={roleLocked}
                            onChange={(e) => setRole(e.target.value as 'Client' | 'Staff')}
                            className={inputClass}
                        >
                            <option value="Client">Client</option>
                            <option value="Staff">Staff (internal)</option>
                        </select>
                    </div>
                    <div>
                        <label className="block text-xs font-bold text-slate-700 mb-1">Deadline</label>
                        <input type="date" value={deadline} onChange={(e) => setDeadline(e.target.value)} className={inputClass} />
                    </div>
                </div>
                <div className="flex items-center justify-end gap-3 pt-3 border-t border-slate-100">
                    <button type="button" onClick={onClose} className="px-4 py-2 text-xs font-semibold text-slate-600 hover:text-slate-800 transition">
                        Close
                    </button>
                    <button
                        type="submit"
                        disabled={isSaving || !title.trim()}
                        className="inline-flex items-center gap-2 px-5 py-2.5 rounded-xl text-xs font-bold text-white bg-indigo-600 hover:bg-indigo-700 shadow-sm transition disabled:opacity-50"
                    >
                        {isSaving ? <Loader2 className="w-3.5 h-3.5 animate-spin" /> : <Edit3 className="w-3.5 h-3.5" />}
                        <span>{isSaving ? 'Saving...' : 'Save Changes'}</span>
                    </button>
                </div>
            </form>
        </ModalShell>
    );
};

interface CancelTaskModalProps {
    action: ClientAction;
    engagementId: string;
    tenantId: string;
    onClose: () => void;
    onCancelled: () => void | Promise<void>;
}

/** Cancel a task that is no longer required. It is kept as "No longer required", never deleted. */
export const CancelTaskModal: React.FC<CancelTaskModalProps> = ({ action, engagementId, tenantId, onClose, onCancelled }) => {
    const [reason, setReason] = useState('');
    const [isCancelling, setIsCancelling] = useState(false);
    const [error, setError] = useState<string | null>(null);

    const handleSubmit = async (e: React.FormEvent) => {
        e.preventDefault();
        if (!reason.trim()) return;

        setIsCancelling(true);
        setError(null);
        try {
            await WorkflowApi.cancelAction(engagementId, action.actionId, reason.trim(), tenantId);
            await onCancelled();
            onClose();
        } catch (err: any) {
            setError(err?.message || 'Failed to cancel task.');
        } finally {
            setIsCancelling(false);
        }
    };

    return (
        <ModalShell
            icon={<div className="p-2 rounded-xl bg-rose-50 text-rose-600 border border-rose-100"><Ban className="w-5 h-5" /></div>}
            title="Cancel Task"
            subtitle="The task stays in the history as “No longer required”."
            error={error}
            onClose={onClose}
        >
            <p className="text-xs text-slate-600">
                Cancelling <strong>"{action.title}"</strong> removes it as a blocker for this stage. This cannot be undone.
            </p>
            <form onSubmit={handleSubmit} className="space-y-3.5">
                <div>
                    <label className="block text-xs font-bold text-slate-700 mb-1">
                        Reason <span className="text-rose-500">*</span>
                    </label>
                    <textarea
                        rows={3}
                        required
                        value={reason}
                        onChange={(e) => setReason(e.target.value)}
                        placeholder="Why is this task no longer required? (recorded in the audit log)"
                        className={inputClass}
                    />
                </div>
                <div className="flex items-center justify-end gap-3 pt-3 border-t border-slate-100">
                    <button type="button" onClick={onClose} className="px-4 py-2 text-xs font-semibold text-slate-600 hover:text-slate-800 transition">
                        Keep Task
                    </button>
                    <button
                        type="submit"
                        disabled={isCancelling || !reason.trim()}
                        className="inline-flex items-center gap-2 px-5 py-2.5 rounded-xl text-xs font-bold text-white bg-rose-600 hover:bg-rose-700 shadow-sm transition disabled:opacity-50"
                    >
                        {isCancelling ? <Loader2 className="w-3.5 h-3.5 animate-spin" /> : <Ban className="w-3.5 h-3.5" />}
                        <span>{isCancelling ? 'Cancelling...' : 'Cancel Task'}</span>
                    </button>
                </div>
            </form>
        </ModalShell>
    );
};

interface ApplyChecklistModalProps {
    engagementId: string;
    tenantId: string;
    onClose: () => void;
    /** Called with the number of tasks added. */
    onApplied: (addedCount: number) => void | Promise<void>;
}

/**
 * Standard checklist, reviewed before it is applied: lists exactly the default tasks that would be added
 * (current and later stages, skipping ones already present) and asks for an explicit confirmation, so the
 * generic template isn't added to an engagement by a misclick.
 */
export const ApplyChecklistModal: React.FC<ApplyChecklistModalProps> = ({ engagementId, tenantId, onClose, onApplied }) => {
    const [preview, setPreview] = useState<ClientAction[] | null>(null);
    const [error, setError] = useState<string | null>(null);
    const [confirmed, setConfirmed] = useState(false);
    const [isApplying, setIsApplying] = useState(false);

    useEffect(() => {
        let cancelled = false;
        WorkflowApi.previewStandardChecklist(engagementId, tenantId)
            .then((tasks) => !cancelled && setPreview(tasks))
            .catch((err: any) => !cancelled && setError(err?.message || 'Could not load the checklist preview.'));
        return () => {
            cancelled = true;
        };
    }, [engagementId, tenantId]);

    const stages = preview ? Array.from(new Set(preview.map((t) => t.stageNumber))).sort((a, b) => a - b) : [];

    const handleApply = async () => {
        setIsApplying(true);
        setError(null);
        try {
            const added = await WorkflowApi.applyStandardChecklist(engagementId, tenantId);
            await onApplied(added.length);
            onClose();
        } catch (err: any) {
            setError(err?.message || 'Failed to apply the standard checklist.');
        } finally {
            setIsApplying(false);
        }
    };

    return (
        <ModalShell
            icon={<div className="p-2 rounded-xl bg-amber-50 text-amber-600"><CheckSquare className="w-5 h-5" /></div>}
            title="Apply Standard Checklist"
            subtitle="Review the default tasks before adding them to this engagement."
            error={error}
            onClose={onClose}
        >
            {preview === null && !error ? (
                <div className="flex items-center gap-2 text-xs text-slate-500">
                    <Loader2 className="w-4 h-4 animate-spin" /> Loading the tasks it would add...
                </div>
            ) : preview && preview.length === 0 ? (
                <p className="text-xs text-slate-600">
                    The standard checklist is already applied to this engagement's current and later stages — nothing would be added.
                </p>
            ) : preview ? (
                <div className="space-y-3">
                    <div className="p-3 rounded-xl bg-amber-50 border border-amber-200 text-[11px] text-amber-900 space-y-1">
                        <p className="font-bold flex items-center gap-1.5">
                            <AlertTriangle className="w-3.5 h-3.5" />
                            This adds {preview.length} generic template task{preview.length === 1 ? '' : 's'} to stage{stages.length === 1 ? '' : 's'} {stages.join(', ')}.
                        </p>
                        <p>Their deadlines are fixed from today, not from when each stage starts.</p>
                        <p>Client tasks appear in the client's portal (as a preview until their stage opens).</p>
                        <p>They are not removed as a group: you would have to edit or cancel each one individually.</p>
                    </div>
                    <div className="max-h-64 overflow-y-auto space-y-2 pr-1">
                        {stages.map((stage) => (
                            <div key={stage}>
                                <div className="text-[10px] font-bold uppercase tracking-wider text-slate-500 mb-1">Stage {stage}</div>
                                <ul className="space-y-1">
                                    {preview.filter((t) => t.stageNumber === stage).map((t) => (
                                        <li key={t.actionId} className="p-2 rounded-lg bg-slate-50 border border-slate-200 text-[11px] flex items-start justify-between gap-2">
                                            <span className="font-semibold text-slate-800">{t.title}</span>
                                            <span className="text-slate-500 shrink-0">
                                                {resolveRole(t)} · {t.type}
                                                {t.deadlineUtc ? ` · due ${new Date(t.deadlineUtc).toLocaleDateString()}` : ''}
                                            </span>
                                        </li>
                                    ))}
                                </ul>
                            </div>
                        ))}
                    </div>
                    <label className="flex items-center gap-2 text-xs text-slate-700 cursor-pointer select-none">
                        <input type="checkbox" checked={confirmed} onChange={(e) => setConfirmed(e.target.checked)} />
                        I've reviewed these tasks and want to add them to this engagement.
                    </label>
                </div>
            ) : null}

            <div className="flex items-center justify-end gap-3 pt-3 border-t border-slate-100">
                <button type="button" onClick={onClose} className="px-4 py-2 text-xs font-semibold text-slate-600 hover:text-slate-800 transition">
                    {preview && preview.length === 0 ? 'Close' : 'Cancel'}
                </button>
                {preview && preview.length > 0 && (
                    <button
                        type="button"
                        onClick={handleApply}
                        disabled={!confirmed || isApplying}
                        className="px-4 py-2 rounded-xl bg-amber-600 hover:bg-amber-700 text-white text-xs font-bold flex items-center gap-2 transition disabled:opacity-40 disabled:cursor-not-allowed"
                    >
                        {isApplying ? <Loader2 className="w-4 h-4 animate-spin" /> : <CheckSquare className="w-4 h-4" />}
                        Add {preview.length} task{preview.length === 1 ? '' : 's'}
                    </button>
                )}
            </div>
        </ModalShell>
    );
};
