import React, { useState, useEffect, useCallback } from 'react';
import { WorkflowApi, ApiError } from '../services/api';
import { Meeting } from '../types';
import { AlertTriangle, RefreshCw, Calendar } from 'lucide-react';

interface MissedMeetingsPanelProps {
    tenantId: string;
}

export const MissedMeetingsPanel: React.FC<MissedMeetingsPanelProps> = ({ tenantId }) => {
    const [meetings, setMeetings] = useState<Meeting[]>([]);
    const [isLoading, setIsLoading] = useState(true);
    const [error, setError] = useState<string | null>(null);

    const load = useCallback(async () => {
        if (!tenantId) return;
        setIsLoading(true);
        setError(null);
        try {
            setMeetings(await WorkflowApi.listMissedMeetings(tenantId));
        } catch (err: any) {
            setError(err instanceof ApiError ? err.message : 'Failed to load missed meetings.');
        } finally {
            setIsLoading(false);
        }
    }, [tenantId]);

    useEffect(() => { load(); }, [load]);

    if (isLoading && meetings.length === 0) return null;

    if (error) {
        return (
            <div className="p-3 rounded-xl bg-rose-50 border border-rose-200 text-xs text-rose-800">
                Missed meetings: {error}
            </div>
        );
    }

    if (meetings.length === 0) return null;

    return (
        <div className="bg-white/85 backdrop-blur-md rounded-2xl border border-amber-200/80 shadow-xs p-5 space-y-3">
            <div className="flex items-center justify-between">
                <div className="flex items-center gap-2">
                    <div className="p-1.5 rounded-lg bg-amber-50 text-amber-600">
                        <AlertTriangle className="w-4 h-4" />
                    </div>
                    <div>
                        <h3 className="text-sm font-bold text-slate-900">Important Meetings Missed</h3>
                        <p className="text-[11px] text-slate-500">
                            {meetings.length} important meeting{meetings.length === 1 ? '' : 's'} past due or missed
                        </p>
                    </div>
                </div>
                <button onClick={load} disabled={isLoading}
                    className="p-2 rounded-lg border border-slate-200 hover:bg-slate-50 text-slate-600 transition disabled:opacity-50">
                    <RefreshCw className={`w-3.5 h-3.5 ${isLoading ? 'animate-spin' : ''}`} />
                </button>
            </div>

            <div className="space-y-2">
                {meetings.map(m => (
                    <div key={m.meetingId}
                        className="p-3 rounded-xl border border-amber-100 bg-amber-50/40 flex items-start gap-3">
                        <Calendar className="w-4 h-4 text-amber-600 shrink-0 mt-0.5" />
                        <div className="min-w-0">
                            <div className="text-sm font-semibold text-slate-900 truncate">{m.purpose}</div>
                            <div className="text-[11px] text-slate-600 mt-0.5">
                                Scheduled for {new Date(m.scheduledAtUtc).toLocaleString()}
                                {m.status === 'Missed' ? ' · marked missed' : ' · past due'}
                            </div>
                            <div className="text-[10px] font-mono text-slate-400 mt-0.5">
                                ENG-{m.engagementId.slice(0, 6).toUpperCase()}
                            </div>
                        </div>
                    </div>
                ))}
            </div>
        </div>
    );
};

export default MissedMeetingsPanel;