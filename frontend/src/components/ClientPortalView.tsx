import React, { useState, useEffect } from 'react';
import { useAuth } from '../context/AuthContext';
import { ClientAction } from '../types';
import { WorkflowApi } from '../services/api';

export const ClientPortalView: React.FC = () => {
    const { tenantId } = useAuth();
    const [engagementId, setEngagementId] = useState<string>('eng-1001');
    const [actions, setActions] = useState<ClientAction[]>([]);
    const [loading, setLoading] = useState<boolean>(true);
    const [error, setError] = useState<string | null>(null);

    const fetchActions = async () => {
        if (!engagementId) return;
        setLoading(true);
        setError(null);
        try {
            const data = await WorkflowApi.getActions(engagementId, tenantId);
            // Filter client-facing actions (is_internal_only = false)
            setActions(data.filter(a => !a.isInternalOnly));
        } catch (err: any) {
            setError(err.message || 'Failed to load client actions');
        } finally {
            setLoading(false);
        }
    };

    useEffect(() => {
        fetchActions();
    }, [engagementId, tenantId]);

    const handleComplete = async (actionId: string) => {
        try {
            await WorkflowApi.completeAction(actionId, tenantId);
            fetchActions();
        } catch (err: any) {
            alert('Error completing action: ' + err.message);
        }
    };

    return (
        <div className="section-container">
            <div className="section-header">
                <div>
                    <h2>Client Action Portal</h2>
                    <p className="section-desc">Guided onboarding requests and document collection tasks for your active engagement.</p>
                </div>
                <div className="engagement-picker">
                    <label>Engagement ID:</label>
                    <input
                        type="text"
                        value={engagementId}
                        onChange={(e) => setEngagementId(e.target.value)}
                        className="form-input font-mono"
                        placeholder="e.g. eng-1001"
                    />
                </div>
            </div>

            {error && (
                <div className="alert alert-warning">
                    <strong>Notice:</strong> {error}. Showing demonstration portal state.
                </div>
            )}

            {loading ? (
                <div className="loading-skeleton">Loading guided tasks for client...</div>
            ) : actions.length === 0 ? (
                <div className="empty-card">
                    <div className="empty-icon">🎉</div>
                    <h3>All Caught Up!</h3>
                    <p>There are currently no pending client action items for engagement <strong>{engagementId}</strong>.</p>
                </div>
            ) : (
                <div className="action-grid">
                    {actions.map((act) => (
                        <div key={act.actionId} className={`action-card ${act.isCompleted ? 'completed' : ''}`}>
                            <div className="action-card-header">
                                <span className="action-type-tag">{act.type}</span>
                                {act.isCompleted ? (
                                    <span className="badge badge-closed">✅ Completed</span>
                                ) : (
                                    <span className="badge badge-inprogress">⏳ Pending Request</span>
                                )}
                            </div>
                            <h3 className="action-title">{act.title}</h3>
                            <p className="action-desc">{act.description}</p>

                            <div className="action-footer">
                                {!act.isCompleted ? (
                                    <button
                                        className="btn btn-primary btn-block"
                                        onClick={() => handleComplete(act.actionId)}
                                    >
                                        ✓ Mark Action as Completed
                                    </button>
                                ) : (
                                    <p className="completion-date text-muted">
                                        Completed on {act.completedAt ? new Date(act.completedAt).toLocaleDateString() : 'Today'}
                                    </p>
                                )}
                            </div>
                        </div>
                    ))}
                </div>
            )}
        </div>
    );
};
