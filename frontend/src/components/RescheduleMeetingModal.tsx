import React, { useState, useEffect } from 'react';
import { createPortal } from 'react-dom';
import { WorkflowApi, ApiError } from '../services/api';
import { Meeting } from '../types';
import { Calendar, X, AlertTriangle, Loader2 } from 'lucide-react';
import { toDate } from '../utils/formatDate';

export interface RescheduleMeetingModalProps {
    meeting: Meeting;              // the meeting being rescheduled
    engagementId: string;
    tenantId: string;
    onClose: () => void;
    onRescheduled: () => void;     // called after successful API call
}

const getLocalDate = (utcString?: string | null): string => {
    const d = toDate(utcString);
    if (!d) return '';
    const pad = (n: number) => n.toString().padStart(2, '0');
    return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
};

const getLocalTime = (utcString?: string | null): string => {
    const d = toDate(utcString);
    if (!d) return '';
    const pad = (n: number) => n.toString().padStart(2, '0');
    return `${pad(d.getHours())}:${pad(d.getMinutes())}`;
};

export const RescheduleMeetingModal: React.FC<RescheduleMeetingModalProps> = ({
    meeting,
    engagementId,
    tenantId,
    onClose,
    onRescheduled,
}) => {
    const [date, setDate] = useState(() => getLocalDate(meeting.scheduledAtUtc));
    const [time, setTime] = useState(() => getLocalTime(meeting.scheduledAtUtc));
    const [duration, setDuration] = useState<string>(() =>
        meeting.durationMinutes != null ? String(meeting.durationMinutes) : ''
    );
    const [reason, setReason] = useState<string>('');
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

    const handleSubmit = async (e: React.FormEvent) => {
        e.preventDefault();
        setError(null);

        if (!date.trim() || !time.trim()) {
            setError('Both date and time are required.');
            return;
        }

        const [year, month, day] = date.split('-').map(Number);
        const [hours, minutes] = time.split(':').map(Number);
        const scheduledDate = new Date(year, month - 1, day, hours, minutes);

        if (isNaN(scheduledDate.getTime()) || scheduledDate.getTime() <= Date.now()) {
            setError('Scheduled time must be in the future.');
            return;
        }

        let parsedDuration: number | null = null;
        if (duration.trim() !== '') {
            const num = Number(duration);
            if (!Number.isInteger(num) || num <= 0) {
                setError('Duration must be a positive integer.');
                return;
            }
            parsedDuration = num;
        }

        setIsSubmitting(true);
        try {
            await WorkflowApi.rescheduleMeeting(
                engagementId,
                meeting.meetingId,
                {
                    newScheduledAtUtc: scheduledDate.toISOString(),
                    durationMinutes: parsedDuration,
                    reason: reason.trim() || null,
                },
                tenantId
            );
            onRescheduled();
            onClose();
        } catch (err: any) {
            setError(err instanceof ApiError ? err.message : (err?.message || 'Failed to reschedule meeting.'));
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
                        <div className="p-2 rounded-xl bg-amber-50 text-amber-600 border border-amber-100">
                            <Calendar className="w-5 h-5" />
                        </div>
                        <div>
                            <h3 className="text-base font-bold text-slate-900">Reschedule Meeting</h3>
                            <p className="text-xs text-slate-500 truncate max-w-[260px]" title={meeting.purpose}>
                                {meeting.purpose}
                            </p>
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

                {/* Error Banner */}
                {error && (
                    <div className="p-3 bg-red-50 border border-red-200 rounded-xl text-red-700 text-xs font-semibold flex items-start gap-2">
                        <AlertTriangle className="w-4 h-4 shrink-0 mt-0.5" />
                        <span>{error}</span>
                    </div>
                )}

                {/* Form */}
                <form onSubmit={handleSubmit} className="space-y-3.5">
                    <div className="grid grid-cols-2 gap-3">
                        <div>
                            <label className="block text-xs font-bold text-slate-700 mb-1">
                                New Date <span className="text-rose-500">*</span>
                            </label>
                            <input
                                type="date"
                                required
                                value={date}
                                onChange={(e) => {
                                    setDate(e.target.value);
                                    if (error) setError(null);
                                }}
                                disabled={isSubmitting}
                                className="w-full text-xs bg-slate-50 border border-slate-200 rounded-xl p-2.5 text-slate-800 focus:outline-none focus:ring-2 focus:ring-indigo-500/20 focus:border-indigo-500"
                            />
                        </div>
                        <div>
                            <label className="block text-xs font-bold text-slate-700 mb-1">
                                New Time <span className="text-rose-500">*</span>
                            </label>
                            <input
                                type="time"
                                required
                                value={time}
                                onChange={(e) => {
                                    setTime(e.target.value);
                                    if (error) setError(null);
                                }}
                                disabled={isSubmitting}
                                className="w-full text-xs bg-slate-50 border border-slate-200 rounded-xl p-2.5 text-slate-800 focus:outline-none focus:ring-2 focus:ring-indigo-500/20 focus:border-indigo-500"
                            />
                        </div>
                    </div>

                    <div>
                        <label className="block text-xs font-bold text-slate-700 mb-1">
                            Duration (minutes)
                        </label>
                        <input
                            type="number"
                            min={1}
                            placeholder="e.g. 30"
                            value={duration}
                            onChange={(e) => {
                                setDuration(e.target.value);
                                if (error) setError(null);
                            }}
                            disabled={isSubmitting}
                            className="w-full text-xs bg-slate-50 border border-slate-200 rounded-xl p-2.5 text-slate-800 focus:outline-none focus:ring-2 focus:ring-indigo-500/20 focus:border-indigo-500"
                        />
                    </div>

                    <div>
                        <label className="block text-xs font-bold text-slate-700 mb-1">
                            Reason (optional)
                        </label>
                        <textarea
                            rows={3}
                            maxLength={2000}
                            placeholder="Reason for rescheduling..."
                            value={reason}
                            onChange={(e) => {
                                setReason(e.target.value);
                                if (error) setError(null);
                            }}
                            disabled={isSubmitting}
                            className="w-full text-xs bg-slate-50 border border-slate-200 rounded-xl p-2.5 text-slate-800 focus:outline-none focus:ring-2 focus:ring-indigo-500/20 focus:border-indigo-500"
                        />
                    </div>

                    <div className="flex items-center justify-end gap-3 pt-3 border-t border-slate-100">
                        <button
                            type="button"
                            onClick={onClose}
                            disabled={isSubmitting}
                            className="px-4 py-2 text-xs font-semibold text-slate-600 hover:text-slate-800 rounded-xl border border-slate-200 hover:bg-slate-50 transition disabled:opacity-50"
                        >
                            Cancel
                        </button>
                        <button
                            type="submit"
                            disabled={isSubmitting || !date || !time}
                            className="inline-flex items-center gap-2 px-5 py-2.5 rounded-xl text-xs font-bold text-white bg-indigo-600 hover:bg-indigo-700 shadow-sm transition disabled:opacity-50"
                        >
                            {isSubmitting ? (
                                <>
                                    <Loader2 className="w-3.5 h-3.5 animate-spin" />
                                    <span>Rescheduling...</span>
                                </>
                            ) : (
                                'Reschedule meeting'
                            )}
                        </button>
                    </div>
                </form>
            </div>
        </div>,
        document.body
    );
};

export default RescheduleMeetingModal;
