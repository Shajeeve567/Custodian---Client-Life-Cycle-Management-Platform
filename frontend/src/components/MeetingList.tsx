import React, { useState, useEffect, useCallback } from 'react';
import { WorkflowApi, ApiError } from '../services/api';
import { Meeting, CreateMeetingRequest } from '../types';
import { formatDateTime, formatDuration } from '../utils/formatDate';
import { RescheduleMeetingModal } from './RescheduleMeetingModal';
import { Calendar, Clock, Plus, RefreshCw, Loader2, X, AlertTriangle, Link2, Users } from 'lucide-react';

interface MeetingListProps {
    engagementId: string;
    tenantId: string;
}

type ModalMode = 'create' | 'edit' | null;

/** ISO timestamp -> value for <input type="datetime-local"> in the viewer's local time. */
const toLocalInput = (iso: string): string => {
    const d = new Date(iso);
    const pad = (n: number) => String(n).padStart(2, '0');
    return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
};

const parseParticipants = (raw: string) => raw.split(',').map(p => p.trim()).filter(Boolean);

export const MeetingList: React.FC<MeetingListProps> = ({ engagementId, tenantId }) => {
    const [meetings, setMeetings] = useState<Meeting[]>([]);
    const [isLoading, setIsLoading] = useState(true);
    const [error, setError] = useState<string | null>(null);

    const [modalMode, setModalMode] = useState<ModalMode>(null);
    const [isSubmitting, setIsSubmitting] = useState(false);
    const [formError, setFormError] = useState<string | null>(null);
    const [meetingToReschedule, setMeetingToReschedule] = useState<Meeting | null>(null);

    // Create form fields
    const [purpose, setPurpose] = useState('');
    const [scheduledAt, setScheduledAt] = useState('');
    const [duration, setDuration] = useState<number | ''>(30);
    const [participantsRaw, setParticipantsRaw] = useState('');
    const [type, setType] = useState<'Normal' | 'Intervention'>('Normal');
    const [importance, setImportance] = useState<'Normal' | 'Important'>('Normal');
    // Edit mode: the meeting being edited, and the reason asked for when its time changes.
    const [editing, setEditing] = useState<Meeting | null>(null);
    const [rescheduleReason, setRescheduleReason] = useState('');

    const load = useCallback(async () => {
        if (!tenantId) return;
        setIsLoading(true);
        setError(null);
        try {
            const rows = await WorkflowApi.listMeetings(engagementId, tenantId);
            setMeetings(rows);
        } catch (err: any) {
            setError(err instanceof ApiError ? err.message : 'Failed to load meetings.');
        } finally {
            setIsLoading(false);
        }
    }, [engagementId, tenantId]);

    useEffect(() => { load(); }, [load]);

    const resetForm = () => {
        setPurpose('');
        setScheduledAt('');
        setDuration(30);
        setParticipantsRaw('');
        setType('Normal');
        setImportance('Normal');
        setEditing(null);
        setRescheduleReason('');
        setFormError(null);
        setIsSubmitting(false);
    };

    const openCreate = () => {
        resetForm();
        setModalMode('create');
    };

    const openEdit = (meeting: Meeting) => {
        resetForm();
        setEditing(meeting);
        setPurpose(meeting.purpose);
        setScheduledAt(toLocalInput(meeting.scheduledAtUtc));
        setDuration(meeting.durationMinutes ?? '');
        setParticipantsRaw(meeting.participants.join(', '));
        setType(meeting.type === 'Intervention' ? 'Intervention' : 'Normal');
        setImportance(meeting.importance === 'Important' ? 'Important' : 'Normal');
        setModalMode('edit');
    };

    // True while editing and the date/time differs from the meeting's current slot.
    const timeChanged = Boolean(editing) && scheduledAt !== '' && scheduledAt !== toLocalInput(editing!.scheduledAtUtc);

    const closeModal = () => {
        if (isSubmitting) return;
        resetForm();
        setModalMode(null);
    };

    const handleCreate = async (e: React.FormEvent) => {
        e.preventDefault();
        setFormError(null);

        if (!purpose.trim()) { setFormError('Purpose is required.'); return; }
        if (!scheduledAt) { setFormError('Scheduled time is required.'); return; }

        const parsed = new Date(scheduledAt);
        if (isNaN(parsed.getTime())) { setFormError('Scheduled time is invalid.'); return; }
        if (parsed.getTime() <= Date.now()) { setFormError('Scheduled time must be in the future.'); return; }

        setIsSubmitting(true);
        try {
            const body: CreateMeetingRequest = {
                type,
                purpose: purpose.trim(),
                scheduledAtUtc: parsed.toISOString(),
                durationMinutes: duration === '' ? null : Number(duration),
                participants: parseParticipants(participantsRaw),
                importance,
            };
            await WorkflowApi.createMeeting(engagementId, body, tenantId);
            resetForm();
            setModalMode(null);
            await load();
        } catch (err: any) {
            setFormError(err instanceof ApiError ? err.message : 'Failed to create meeting.');
        } finally {
            setIsSubmitting(false);
        }
    };

    const handleStatus = async (meetingId: string, status: 'Completed' | 'Cancelled' | 'Missed') => {
        try {
            await WorkflowApi.updateMeetingStatus(engagementId, meetingId, status, tenantId);
            await load();
        } catch (err: any) {
            alert(err instanceof ApiError ? err.message : 'Failed to update status.');
        }
    };

    /**
     * One Edit dialog for everything. A new date/time goes through reschedule (keeps the original
     * meeting, links the new one and records the reason in the audit log); other details go through
     * edit, which updates the meeting in place and is audited as "Meeting updated".
     */
    const handleEdit = async (e: React.FormEvent) => {
        e.preventDefault();
        if (!editing) return;
        setFormError(null);

        if (!purpose.trim()) { setFormError('Purpose is required.'); return; }
        const parsed = new Date(scheduledAt);
        if (isNaN(parsed.getTime())) { setFormError('Scheduled time is invalid.'); return; }
        if (timeChanged && parsed.getTime() <= Date.now()) { setFormError('The new time must be in the future.'); return; }

        const durationMinutes = duration === '' ? null : Number(duration);
        const participants = parseParticipants(participantsRaw);
        const detailsChanged =
            purpose.trim() !== editing.purpose ||
            participants.join(',').toLowerCase() !== editing.participants.join(',').toLowerCase() ||
            importance !== editing.importance ||
            (!timeChanged && durationMinutes !== (editing.durationMinutes ?? null));

        if (!timeChanged && !detailsChanged) { closeModal(); return; }

        setIsSubmitting(true);
        try {
            let target = editing;
            if (timeChanged) {
                target = await WorkflowApi.rescheduleMeeting(engagementId, editing.meetingId, {
                    newScheduledAtUtc: parsed.toISOString(),
                    durationMinutes,
                    reason: rescheduleReason.trim() || undefined,
                }, tenantId);
            }
            if (detailsChanged) {
                await WorkflowApi.updateMeeting(engagementId, target.meetingId, {
                    purpose: purpose.trim(),
                    scheduledAtUtc: target.scheduledAtUtc, // unchanged here; a new time was handled above
                    durationMinutes, // the value in the form (after a reschedule the new meeting already has it)
                    participants,
                    importance,
                }, tenantId);
            }
            resetForm();
            setModalMode(null);
            await load();
        } catch (err: any) {
            setFormError(err instanceof ApiError ? err.message : 'Failed to save the meeting.');
            await load();
        } finally {
            setIsSubmitting(false);
        }
    };

    const statusBadge = (status: string) => {
        const cls = status === 'Completed' ? 'bg-emerald-50 text-emerald-700 border-emerald-200'
            : status === 'Missed' ? 'bg-rose-50 text-rose-700 border-rose-200'
            : status === 'Cancelled' ? 'bg-slate-100 text-slate-600 border-slate-200'
            : status === 'Rescheduled' ? 'bg-amber-50 text-amber-700 border-amber-200'
            : 'bg-indigo-50 text-indigo-700 border-indigo-200';
        return <span className={`inline-flex px-2 py-0.5 rounded-full text-[10px] font-bold border ${cls}`}>{status}</span>;
    };

    const activeMeetings = meetings.filter(m => m.status !== 'Rescheduled');
    const rescheduledMeetings = meetings.filter(m => m.status === 'Rescheduled');

    return (
        <div className="bg-white/85 backdrop-blur-md rounded-2xl border border-slate-200/80 shadow-xs p-5 space-y-4">
            <div className="flex flex-wrap items-start justify-between gap-3 pb-3 border-b border-slate-100">
                <div className="min-w-0">
                    <h3 className="text-base font-bold text-slate-900 flex items-center gap-2">
                        <Calendar className="w-4 h-4 text-indigo-600" />
                        Meetings
                        {meetings.length > 0 && (
                            <span className="px-2 py-0.5 rounded-full text-[10px] font-bold bg-slate-100 text-slate-600 border border-slate-200">
                                {meetings.length}
                            </span>
                        )}
                    </h3>
                    <p className="text-xs text-slate-500 mt-0.5">
                        Schedule and manage client discussions for this engagement. Times are shown in your local time.
                    </p>
                </div>
                <div className="flex items-center gap-2 shrink-0">
                    <button onClick={load} disabled={isLoading}
                        className="p-2 rounded-lg border border-slate-200 hover:bg-slate-50 text-slate-600 transition disabled:opacity-50"
                        title="Refresh" aria-label="Refresh meetings">
                        <RefreshCw className={`w-3.5 h-3.5 ${isLoading ? 'animate-spin' : ''}`} />
                    </button>
                    <button onClick={openCreate}
                        className="px-3 py-2 rounded-lg bg-indigo-600 hover:bg-indigo-700 text-white text-xs font-bold transition inline-flex items-center gap-1.5">
                        <Plus className="w-3.5 h-3.5" />
                        Schedule Meeting
                    </button>
                </div>
            </div>

            {isLoading && meetings.length === 0 ? (
                <div className="py-8 text-center space-y-2">
                    <Loader2 className="w-5 h-5 animate-spin text-indigo-600 mx-auto" />
                    <p className="text-xs text-slate-500">Loading meetings...</p>
                </div>
            ) : error ? (
                <div className="p-3 rounded-xl bg-rose-50 border border-rose-200 text-xs text-rose-800">{error}</div>
            ) : meetings.length === 0 ? (
                <div className="py-8 text-center border border-dashed border-slate-300 rounded-xl space-y-2">
                    <p className="text-xs text-slate-500">No meetings scheduled yet.</p>
                    <button onClick={openCreate}
                        className="text-xs font-semibold text-indigo-600 hover:text-indigo-800 hover:underline">
                        Schedule the first meeting
                    </button>
                </div>
            ) : (
                <ul className="space-y-3">
                    {meetings.map(m => {
                        const when = new Date(m.scheduledAtUtc);
                        const isPastDue = m.status === 'Scheduled' && when.getTime() < Date.now();
                        return (
                            <li key={m.meetingId}
                                className={`p-4 rounded-xl border bg-white flex flex-col lg:flex-row lg:items-center justify-between gap-4 ${isPastDue ? 'border-amber-200' : 'border-slate-200'}`}>
                                <div className="flex items-start gap-3 min-w-0 flex-1">
                                    {/* Date block: the first thing people look for */}
                                    <div className="shrink-0 w-14 rounded-lg border border-indigo-100 bg-indigo-50/60 text-center py-1.5">
                                        <div className="text-[10px] font-bold uppercase text-indigo-600">
                                            {when.toLocaleDateString([], { month: 'short' })}
                                        </div>
                                        <div className="text-lg font-bold text-slate-900 leading-tight">{when.getDate()}</div>
                                        <div className="text-[10px] text-slate-500">{when.toLocaleDateString([], { weekday: 'short' })}</div>
                                    </div>
                                    <div className="min-w-0 space-y-1">
                                        <div className="flex flex-wrap items-center gap-2">
                                            <span className="font-semibold text-slate-900 text-sm break-words">{m.purpose}</span>
                                            {statusBadge(m.status)}
                                            {isPastDue && (
                                                <span className="inline-flex px-2 py-0.5 rounded-full text-[10px] font-bold border bg-amber-50 text-amber-700 border-amber-200">Past due</span>
                                            )}
                                            {m.importance === 'Important' && (
                                                <span className="inline-flex px-2 py-0.5 rounded-full text-[10px] font-bold border bg-amber-100 text-amber-800 border-amber-200">Important</span>
                                            )}
                                            {m.type === 'Intervention' && (
                                                <span className="inline-flex px-2 py-0.5 rounded-full text-[10px] font-bold border bg-violet-50 text-violet-700 border-violet-200">Intervention</span>
                                            )}
                                        </div>
                                        <div className="text-xs text-slate-600 flex flex-wrap items-center gap-x-3 gap-y-1">
                                            <span className="inline-flex items-center gap-1">
                                                <Clock className="w-3.5 h-3.5 text-slate-400" />
                                                {when.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}
                                                {m.durationMinutes ? ` · ${m.durationMinutes} min` : ''}
                                            </span>
                                            {m.participants.length > 0 && (
                                                <span className="inline-flex items-center gap-1 min-w-0 break-all">
                                                    <Users className="w-3.5 h-3.5 text-slate-400 shrink-0" />
                                                    {m.participants.join(', ')}
                                                </span>
                                            )}
                                            {m.rescheduledFromMeetingId && (
                                                <span className="inline-flex items-center gap-1 text-slate-500">
                                                    <Link2 className="w-3 h-3" /> Rescheduled
                                                </span>
                                            )}
                                        </div>
                                        {m.rescheduleReason && (
                                            <div className="text-[11px] text-slate-500 italic break-words">
                                                Reason: {m.rescheduleReason}
                                            </div>
                                        )}
                                    </div>
                                </div>
                                {m.status === 'Scheduled' && (
                                    <div className="flex flex-wrap items-center gap-1.5 lg:justify-end shrink-0">
                                        <button onClick={() => handleStatus(m.meetingId, 'Completed')}
                                            className="px-2.5 py-1.5 rounded-lg bg-emerald-50 hover:bg-emerald-100 border border-emerald-200 text-emerald-800 text-xs font-semibold">
                                            Complete
                                        </button>
                                        <button onClick={() => handleStatus(m.meetingId, 'Missed')}
                                            className="px-2.5 py-1.5 rounded-lg bg-rose-50 hover:bg-rose-100 border border-rose-200 text-rose-800 text-xs font-semibold">
                                            Missed
                                        </button>
                                        <button onClick={() => openEdit(m)}
                                            className="px-2.5 py-1.5 rounded-lg bg-indigo-50 hover:bg-indigo-100 border border-indigo-200 text-indigo-700 text-xs font-semibold">
                                            Edit
                                        </button>
                                        <button onClick={() => handleStatus(m.meetingId, 'Cancelled')}
                                            className="px-2.5 py-1.5 rounded-lg bg-white hover:bg-slate-50 border border-slate-200 text-slate-700 text-xs font-semibold">
                                            Cancel
                                        </button>
                                    </div>
                                )}
                            </li>
                        );
                    })}
                </ul>
            )}

            {(modalMode === 'create' || modalMode === 'edit') && (
                <div className="fixed inset-0 z-50 flex items-center justify-center p-4 bg-slate-900/50 backdrop-blur-xs">
                    <div className="fixed inset-0" onClick={closeModal} />
                    <div className="relative z-10 bg-white rounded-2xl shadow-xl border border-slate-200 max-w-lg w-full p-6 space-y-4">
                        <div className="flex items-center justify-between pb-3 border-b border-slate-100">
                            <h2 className="text-lg font-bold text-slate-900">{modalMode === 'edit' ? 'Edit Meeting' : 'Schedule Meeting'}</h2>
                            <button onClick={closeModal} disabled={isSubmitting} aria-label="Close"
                                className="p-1 rounded-lg text-slate-400 hover:text-slate-700">
                                <X className="w-5 h-5" />
                            </button>
                        </div>

                        {formError && (
                            <div className="p-3 bg-red-50 border border-red-200 rounded-xl text-xs text-red-700 flex items-start gap-2">
                                <AlertTriangle className="w-4 h-4 shrink-0 mt-0.5" />
                                <span>{formError}</span>
                            </div>
                        )}

                        <form onSubmit={modalMode === 'edit' ? handleEdit : handleCreate} className="space-y-3">
                            <div>
                                <label className="text-[11px] font-bold text-slate-700 uppercase tracking-wider block mb-1">Purpose</label>
                                <input type="text" value={purpose} onChange={e => setPurpose(e.target.value)}
                                    disabled={isSubmitting} maxLength={200}
                                    placeholder="e.g. Onboarding kickoff call"
                                    className="w-full px-3 py-2 rounded-xl border border-slate-200 text-sm focus:outline-none focus:ring-2 focus:ring-indigo-500/20 focus:border-indigo-500" />
                            </div>

                            <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
                                <div>
                                    <label className="text-[11px] font-bold text-slate-700 uppercase tracking-wider block mb-1">When (your local time)</label>
                                    <input type="datetime-local" value={scheduledAt}
                                        onChange={e => setScheduledAt(e.target.value)} disabled={isSubmitting}
                                        className="w-full px-3 py-2 rounded-xl border border-slate-200 text-sm focus:outline-none focus:ring-2 focus:ring-indigo-500/20" />
                                </div>
                                <div>
                                    <label className="text-[11px] font-bold text-slate-700 uppercase tracking-wider block mb-1">Duration (min)</label>
                                    <input type="number" value={duration} min={5} max={480}
                                        onChange={e => setDuration(e.target.value === '' ? '' : Number(e.target.value))}
                                        disabled={isSubmitting}
                                        className="w-full px-3 py-2 rounded-xl border border-slate-200 text-sm focus:outline-none focus:ring-2 focus:ring-indigo-500/20" />
                                </div>
                            </div>

                            {timeChanged && (
                                <div className="p-3 rounded-xl bg-amber-50 border border-amber-200 space-y-2">
                                    <p className="text-xs text-amber-900">
                                        Changing the time reschedules the meeting: the current slot is kept as <strong>Rescheduled</strong> and a new meeting is linked to it.
                                    </p>
                                    <label htmlFor="meeting-reschedule-reason" className="text-[11px] font-bold text-slate-700 uppercase tracking-wider block">
                                        Reason for rescheduling (optional)
                                    </label>
                                    <input id="meeting-reschedule-reason" type="text" value={rescheduleReason}
                                        onChange={e => setRescheduleReason(e.target.value)} disabled={isSubmitting} maxLength={2000}
                                        placeholder="e.g. Client requested a later slot"
                                        className="w-full px-3 py-2 rounded-xl border border-slate-200 bg-white text-sm focus:outline-none focus:ring-2 focus:ring-indigo-500/20" />
                                </div>
                            )}

                            <div>
                                <label className="text-[11px] font-bold text-slate-700 uppercase tracking-wider block mb-1">
                                    Participants (comma separated)
                                </label>
                                <input type="text" value={participantsRaw}
                                    onChange={e => setParticipantsRaw(e.target.value)} disabled={isSubmitting}
                                    placeholder="ops@acme.test, staff@test.local"
                                    className="w-full px-3 py-2 rounded-xl border border-slate-200 text-sm focus:outline-none focus:ring-2 focus:ring-indigo-500/20" />
                            </div>

                            <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
                                <div>
                                    <label className="text-[11px] font-bold text-slate-700 uppercase tracking-wider block mb-1">Type</label>
                                    <select value={type} onChange={e => setType(e.target.value as any)} disabled={isSubmitting || modalMode === 'edit'}
                                        title={modalMode === 'edit' ? "A meeting's type can't be changed after it is scheduled" : undefined}
                                        className="w-full px-3 py-2 rounded-xl border border-slate-200 text-sm">
                                        <option value="Normal">Normal</option>
                                        <option value="Intervention">Intervention</option>
                                    </select>
                                </div>
                                <div>
                                    <label className="text-[11px] font-bold text-slate-700 uppercase tracking-wider block mb-1">Importance</label>
                                    <select value={importance} onChange={e => setImportance(e.target.value as any)} disabled={isSubmitting}
                                        className="w-full px-3 py-2 rounded-xl border border-slate-200 text-sm">
                                        <option value="Normal">Normal</option>
                                        <option value="Important">Important</option>
                                    </select>
                                </div>
                            </div>

                            <div className="flex justify-end gap-2 pt-2 border-t border-slate-100">
                                <button type="button" onClick={closeModal} disabled={isSubmitting}
                                    className="px-4 py-2 rounded-xl border border-slate-200 text-slate-700 text-xs font-semibold hover:bg-slate-50">
                                    Cancel
                                </button>
                                <button type="submit" disabled={isSubmitting}
                                    className="px-5 py-2 rounded-xl bg-indigo-600 hover:bg-indigo-700 text-white text-xs font-bold disabled:opacity-50 inline-flex items-center gap-1.5">
                                    {isSubmitting
                                        ? <><Loader2 className="w-3.5 h-3.5 animate-spin" /> Saving...</>
                                        : modalMode === 'edit' ? (timeChanged ? 'Reschedule & save' : 'Save changes') : 'Schedule'}
                                </button>
                            </div>
                        </form>
                    </div>
                </div>
            )}

            {meetingToReschedule && tenantId && (
                <RescheduleMeetingModal
                    meeting={meetingToReschedule}
                    engagementId={engagementId}
                    tenantId={tenantId}
                    onClose={() => setMeetingToReschedule(null)}
                    onRescheduled={async () => {
                        setMeetingToReschedule(null);
                        await load();
                    }}
                />
            )}
        </div>
    );
};

export default MeetingList;