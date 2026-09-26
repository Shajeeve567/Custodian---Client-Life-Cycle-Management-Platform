import React, { useState } from 'react';
import { WorkflowApi } from '../services/api';
import { ClientAction, UpdateClientActionRequest } from '../types';
import { ENGAGEMENT_STAGES } from '../constants/engagementStages';
import { AlertTriangle, Ban, Edit3, Loader2, X } from 'lucide-react';

/**
 * Staff-defined stage tasks: tasks staff own (Manual / Lifecycle source). Tasks mirroring a
 * requirement or belonging to a condition are managed through their source, never here.
 */
export const isStaffManagedTask = (action: ClientAction): boolean =>
    !action.linkedRequirementId &&
    !action.linkedConditionId &&
    action.sourceType !== 'Requirement' &&
    action.sourceType !== 'Condition';

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
        if (role !== resolveRole(action)) {
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
                        <select value={role} onChange={(e) => setRole(e.target.value as 'Client' | 'Staff')} className={inputClass}>
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
