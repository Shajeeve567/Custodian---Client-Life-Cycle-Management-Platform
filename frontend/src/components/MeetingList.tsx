import React, { useState, useEffect, useCallback } from 'react';
import { WorkflowApi, ApiError } from '../services/api';
import { Meeting, CreateMeetingRequest } from '../types';
import { Calendar, Plus, RefreshCw, Loader2, X, AlertTriangle, Link2 } from 'lucide-react';
import { formatDateTime, formatDuration } from '../utils/formatDate';
import { RescheduleMeetingModal } from './RescheduleMeetingModal';

interface MeetingListProps {
    engagementId: string;
    tenantId: string;
}

type ModalMode = 'create' | null;

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
        setFormError(null);
        setIsSubmitting(false);
    };

    const openCreate = () => {
        resetForm();
        setModalMode('create');
    };

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
                participants: participantsRaw
                    .split(',')
                    .map(p => p.trim())
                    .filter(Boolean),
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

    const handleReschedule = (meeting: Meeting) => {
        setMeetingToReschedule(meeting);
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
            <div className="flex items-center justify-between">
                <div>
                    <h3 className="text-base font-bold text-slate-900 flex items-center gap-2">
                        <Calendar className="w-4 h-4 text-indigo-600" />
                        Meetings
                    </h3>
                    <p className="text-xs text-slate-500 mt-0.5">
                        Schedule and manage client discussions for this engagement.
                    </p>
                </div>
                <div className="flex items-center gap-2">
                    <button onClick={load} disabled={isLoading}
                        className="p-2 rounded-lg border border-slate-200 hover:bg-slate-50 text-slate-600 transition disabled:opacity-50"
                        title="Refresh">
                        <RefreshCw className={`w-3.5 h-3.5 ${isLoading ? 'animate-spin' : ''}`} />
                    </button>
                    <button onClick={openCreate}
                        className="px-3 py-1.5 rounded-lg bg-indigo-600 hover:bg-indigo-700 text-white text-xs font-bold transition inline-flex items-center gap-1.5">
                        <Plus className="w-3.5 h-3.5" />
                        Schedule Meeting
                    </button>
                </div>
            </div>

            {isLoading && meetings.length === 0 ? (
                <div className="py-8 text-center">
                    <Loader2 className="w-5 h-5 animate-spin text-indigo-600 mx-auto" />
                </div>
            ) : error ? (
                <div className="p-3 rounded-xl bg-rose-50 border border-rose-200 text-xs text-rose-800">{error}</div>
            ) : meetings.length === 0 ? (
                <div className="py-8 text-center border border-dashed border-slate-300 rounded-xl">
                    <p className="text-xs text-slate-500">No meetings scheduled yet.</p>
                </div>
            ) : (
                <div className="space-y-3">
                    {/* Main list: only meetings whose status !== 'Rescheduled' */}
                    {activeMeetings.length === 0 ? (
                        <div className="py-6 text-center border border-dashed border-slate-200 rounded-xl">
                            <p className="text-xs text-slate-500">No active meetings scheduled.</p>
                        </div>
                    ) : (
                        <div className="space-y-2">
                            {activeMeetings.map(m => (
                                <div key={m.meetingId} className="p-4 rounded-xl border border-slate-200 bg-white space-y-2">
                                    {/* Row 1: Title (left, truncating) + Status badge (right) */}
                                    <div className="flex items-center justify-between gap-2">
                                        <div className="flex items-center gap-2 min-w-0">
                                            <span className="font-semibold text-slate-900 text-sm truncate" title={m.purpose}>
                                                {m.purpose}
                                            </span>
                                            {m.rescheduledFromMeetingId && (
                                                <span className="inline-flex items-center gap-1 text-[10px] text-slate-500 shrink-0">
                                                    <Link2 className="w-3 h-3" /> rescheduled
                                                </span>
                                            )}
                                        </div>
                                        <div className="shrink-0">{statusBadge(m.status)}</div>
                                    </div>

                                    {/* Row 2: Single line — calendar icon, formatted date, duration, importance */}
                                    <div className="flex items-center gap-1.5 text-xs text-slate-600 truncate">
                                        <Calendar className="w-3.5 h-3.5 text-slate-400 shrink-0" />
                                        <span>{formatDateTime(m.scheduledAtUtc)}</span>
                                        {m.durationMinutes ? (
                                            <>
                                                <span className="text-slate-300">·</span>
                                                <span>{formatDuration(m.durationMinutes)}</span>
                                            </>
                                        ) : null}
                                        {m.importance === 'Important' && (
                                            <>
                                                <span className="text-slate-300">·</span>
                                                <span className="font-bold text-amber-700">Important</span>
                                            </>
                                        )}
                                        {m.type === 'Intervention' && (
                                            <>
                                                <span className="text-slate-300">·</span>
                                                <span className="text-indigo-600 font-medium">Intervention</span>
                                            </>
                                        )}
                                    </div>

                                    {m.participants.length > 0 && (
                                        <div className="text-[11px] text-slate-500 truncate">
                                            With: {m.participants.join(', ')}
                                        </div>
                                    )}

                                    {m.rescheduleReason && (
                                        <div className="text-[11px] text-slate-500 italic truncate">
                                            Reason: {m.rescheduleReason}
                                        </div>
                                    )}

                                    {/* Row 3: Action controls */}
                                    {m.status === 'Scheduled' && (
                                        <div className="flex items-center gap-1.5 pt-2.5 mt-1 border-t border-slate-100">
                                            <button
                                                type="button"
                                                onClick={() => handleStatus(m.meetingId, 'Completed')}
                                                className="px-2.5 py-1 rounded-lg bg-emerald-50 hover:bg-emerald-100 text-emerald-800 text-[11px] font-bold transition"
                                            >
                                                Complete
                                            </button>
                                            <button
                                                type="button"
                                                onClick={() => handleStatus(m.meetingId, 'Missed')}
                                                className="px-2.5 py-1 rounded-lg bg-rose-50 hover:bg-rose-100 text-rose-800 text-[11px] font-bold transition"
                                            >
                                                Missed
                                            </button>
                                            <button
                                                type="button"
                                                onClick={() => handleReschedule(m)}
                                                className="px-2.5 py-1 rounded-lg bg-amber-50 hover:bg-amber-100 text-amber-800 text-[11px] font-bold transition"
                                            >
                                                Reschedule
                                            </button>
                                            <button
                                                type="button"
                                                onClick={() => handleStatus(m.meetingId, 'Cancelled')}
                                                className="ml-auto px-2.5 py-1 rounded-lg bg-slate-100 hover:bg-slate-200 text-slate-700 text-[11px] font-bold transition"
                                            >
                                                Cancel
                                            </button>
                                        </div>
                                    )}
                                </div>
                            ))}
                        </div>
                    )}

                    {/* Rescheduled meetings in compact collapsible details */}
                    {rescheduledMeetings.length > 0 && (
                        <details className="pt-2 text-xs text-slate-600 group border-t border-slate-100">
                            <summary className="cursor-pointer font-medium text-slate-500 hover:text-slate-800 select-none py-1 inline-flex items-center gap-1.5 focus:outline-none">
                                <span>Show {rescheduledMeetings.length} rescheduled meeting{rescheduledMeetings.length === 1 ? '' : 's'}</span>
                            </summary>
                            <div className="mt-2 space-y-1.5 pl-2 border-l-2 border-slate-100">
                                {rescheduledMeetings.map(m => (
                                    <div
                                        key={m.meetingId}
                                        className="flex items-center gap-2 py-1.5 px-2.5 rounded-lg bg-slate-50 text-slate-600 text-xs"
                                    >
                                        <span className="font-medium text-slate-800 truncate" title={m.purpose}>
                                            {m.purpose}
                                        </span>
                                        <span className="text-slate-300 shrink-0">·</span>
                                        <span className="text-slate-500 shrink-0">
                                            was {formatDateTime(m.scheduledAtUtc)}
                                        </span>
                                        <span className="text-slate-300 shrink-0">·</span>
                                        <span className="shrink-0">{statusBadge(m.status)}</span>
                                    </div>
                                ))}
                            </div>
                        </details>
                    )}
                </div>
            )}

            {modalMode === 'create' && (
                <div className="fixed inset-0 z-50 flex items-center justify-center p-4 bg-slate-900/50 backdrop-blur-xs">
                    <div className="fixed inset-0" onClick={closeModal} />
                    <div className="relative z-10 bg-white rounded-2xl shadow-xl border border-slate-200 max-w-lg w-full p-6 space-y-4">
                        <div className="flex items-center justify-between pb-3 border-b border-slate-100">
                            <h2 className="text-lg font-bold text-slate-900">Schedule Meeting</h2>
                            <button onClick={closeModal} disabled={isSubmitting}
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

                        <form onSubmit={handleCreate} className="space-y-3">
                            <div>
                                <label className="text-[11px] font-bold text-slate-700 uppercase tracking-wider block mb-1">Purpose</label>
                                <input type="text" value={purpose} onChange={e => setPurpose(e.target.value)}
                                    disabled={isSubmitting} maxLength={200}
                                    placeholder="e.g. Onboarding kickoff call"
                                    className="w-full px-3 py-2 rounded-xl border border-slate-200 text-sm focus:outline-none focus:ring-2 focus:ring-indigo-500/20 focus:border-indigo-500" />
                            </div>

                            <div className="grid grid-cols-2 gap-3">
                                <div>
                                    <label className="text-[11px] font-bold text-slate-700 uppercase tracking-wider block mb-1">When</label>
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

                            <div>
                                <label className="text-[11px] font-bold text-slate-700 uppercase tracking-wider block mb-1">
                                    Participants (comma separated)
                                </label>
                                <input type="text" value={participantsRaw}
                                    onChange={e => setParticipantsRaw(e.target.value)} disabled={isSubmitting}
                                    placeholder="ops@acme.test, staff@test.local"
                                    className="w-full px-3 py-2 rounded-xl border border-slate-200 text-sm focus:outline-none focus:ring-2 focus:ring-indigo-500/20" />
                            </div>

                            <div className="grid grid-cols-2 gap-3">
                                <div>
                                    <label className="text-[11px] font-bold text-slate-700 uppercase tracking-wider block mb-1">Type</label>
                                    <select value={type} onChange={e => setType(e.target.value as any)} disabled={isSubmitting}
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
                                    {isSubmitting ? <><Loader2 className="w-3.5 h-3.5 animate-spin" /> Saving...</> : 'Schedule'}
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