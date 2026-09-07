import React, { useState, useEffect } from 'react';
import { useAuth } from '../context/AuthContext';
import { DashboardLayout } from '../components/DashboardLayout';
import { StaffActionHistoryView } from '../components/StaffActionHistoryView';
import { WorkflowApi } from '../services/api';
import { Engagement } from '../types';

export const WorkflowsPage: React.FC = () => {
    const { tenantId } = useAuth();
    const [engagements, setEngagements] = useState<Engagement[]>([]);
    const [selectedEngagementId, setSelectedEngagementId] = useState<string>('');

    useEffect(() => {
        if (!tenantId) return;
        WorkflowApi.getEngagements(tenantId)
            .then((list) => {
                setEngagements(list);
                if (list.length > 0) {
                    setSelectedEngagementId(list[0].engagementId);
                }
            })
            .catch((err) => console.error('Failed to load engagements for workflows:', err));
    }, [tenantId]);

    return (
        <DashboardLayout>
            <div className="w-full max-w-5xl">
                <div className="flex items-center justify-between mb-4 bg-white p-4 rounded-xl border border-slate-200">
                    <div>
                        <h2 className="text-base font-bold text-slate-800">Workflow State Engine & Action History</h2>
                        <p className="text-xs text-slate-500">Track and advance pending gate requirements</p>
                    </div>

                    {engagements.length > 0 && (
                        <div className="flex items-center gap-2">
                            <label className="text-xs font-semibold text-slate-600">Select Engagement:</label>
                            <select
                                value={selectedEngagementId}
                                onChange={(e) => setSelectedEngagementId(e.target.value)}
                                className="text-xs border border-slate-300 rounded px-2 py-1.5 bg-white font-mono"
                            >
                                {engagements.map((eng) => (
                                    <option key={eng.engagementId} value={eng.engagementId}>
                                        {eng.engagementId.slice(0, 8)}... ({eng.status})
                                    </option>
                                ))}
                            </select>
                        </div>
                    )}
                </div>

                {selectedEngagementId && tenantId ? (
                    <StaffActionHistoryView
                        engagementId={selectedEngagementId}
                        tenantId={tenantId}
                    />
                ) : (
                    <div className="bg-white p-12 rounded-2xl border border-dashed border-slate-300 text-center space-y-3">
                        <h3 className="text-base font-bold text-slate-800">No Active Engagements</h3>
                        <p className="text-xs text-slate-500 max-w-sm mx-auto">
                            Create an engagement from the Engagements hub to view workflow stage actions and transition history.
                        </p>
                    </div>
                )}
            </div>
        </DashboardLayout>
    );
};
