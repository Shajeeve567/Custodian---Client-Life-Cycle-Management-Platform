import React, { useState, useEffect } from 'react';
import { useAuth } from '../context/AuthContext';
import { Engagement, EngagementStatus } from '../types';
import { WorkflowApi } from '../services/api';

export const EngagementManager: React.FC = () => {
    const { tenantId } = useAuth();
    const [engagements, setEngagements] = useState<Engagement[]>([]);
    const [loading, setLoading] = useState<boolean>(true);
    const [error, setError] = useState<string | null>(null);

    // Create Modal State
    const [showModal, setShowModal] = useState<boolean>(false);
    const [clientId, setClientId] = useState<string>('cli-88');
    const [staffId, setStaffId] = useState<string>('stf-42');
    const [submitting, setSubmitting] = useState<boolean>(false);

    const fetchEngagements = async () => {
        setLoading(true);
        setError(null);
        try {
            const data = await WorkflowApi.getEngagements(tenantId);
            setEngagements(data);
        } catch (err: any) {
            setError(err.message || 'Failed to connect to Workflow Service');
        } finally {
            setLoading(false);
        }
    };

    useEffect(() => {
        fetchEngagements();
    }, [tenantId]);

    const handleCreate = async (e: React.FormEvent) => {
        e.preventDefault();
        setSubmitting(true);
        try {
            await WorkflowApi.createEngagement({ tenantId, clientId, staffId });
            setShowModal(false);
            fetchEngagements();
        } catch (err: any) {
            alert('Error creating engagement: ' + err.message);
        } finally {
            setSubmitting(false);
        }
    };

    const handleStatusChange = async (id: string, newStatus: EngagementStatus) => {
        try {
            await WorkflowApi.updateStatus(id, newStatus, tenantId);
            fetchEngagements();
        } catch (err: any) {
            alert('Failed to update status: ' + err.message);
        }
    };

    const handleDelete = async (engagement: Engagement) => {
        if (engagement.status !== 'Draft') {
            alert(`Policy Enforcement: Physical deletion is prohibited for engagements in '${engagement.status}' state.`);
            return;
        }
        if (!confirm(`Are you sure you want to delete engagement ${engagement.engagementId}?`)) return;

        try {
            await WorkflowApi.deleteEngagement(engagement.engagementId, tenantId);
            fetchEngagements();
        } catch (err: any) {
            alert('Delete failed: ' + err.message);
        }
    };

    const getStatusBadge = (status: EngagementStatus) => {
        switch (status) {
            case 'Draft': return <span className="badge badge-draft">📝 Draft</span>;
            case 'Started': return <span className="badge badge-inprogress">⚡ Started</span>;
            case 'Closed': return <span className="badge badge-closed">🔒 Closed</span>;
            case 'Cancelled': return <span className="badge badge-danger">🚫 Cancelled</span>;
        }
    };

    return (
        <div className="section-container">
            <div className="section-header">
                <div>
                    <h2>Engagement Lifecycle Management</h2>
                    <p className="section-desc">Manage B2B onboarding lifecycles, lifecycle stage transitions, and tenant boundaries.</p>
                </div>
                <button className="btn btn-primary" onClick={() => setShowModal(true)}>
                    ➕ New Engagement
                </button>
            </div>

            {error && (
                <div className="alert alert-warning">
                    <strong>Backend Offline or Error:</strong> {error}. Showing demonstration view state.
                </div>
            )}

            {loading ? (
                <div className="loading-skeleton">Loading engagements for {tenantId}...</div>
            ) : engagements.length === 0 ? (
                <div className="empty-card">
                    <div className="empty-icon">📂</div>
                    <h3>No Engagements Found</h3>
                    <p>There are no active client lifecycle engagements for <strong>{tenantId}</strong>.</p>
                    <button className="btn btn-secondary" onClick={() => setShowModal(true)}>
                        Create First Engagement
                    </button>
                </div>
            ) : (
                <div className="table-wrapper">
                    <table className="data-table">
                        <thead>
                            <tr>
                                <th>Engagement ID</th>
                                <th>Client ID</th>
                                <th>Staff ID</th>
                                <th>Status</th>
                                <th>Created Date</th>
                                <th>Lifecycle Actions</th>
                            </tr>
                        </thead>
                        <tbody>
                            {engagements.map((eng) => (
                                <tr key={eng.engagementId}>
                                    <td className="font-mono">{eng.engagementId}</td>
                                    <td>{eng.clientId}</td>
                                    <td>{eng.staffId}</td>
                                    <td>{getStatusBadge(eng.status)}</td>
                                    <td className="text-muted">{new Date(eng.createdAt).toLocaleString()}</td>
                                    <td className="action-cell">
                                        {eng.status === 'Draft' && (
                                            <button
                                                className="btn btn-sm btn-outline-success"
                                                onClick={() => handleStatusChange(eng.engagementId, 'Started')}
                                            >
                                                Start Lifecycle
                                            </button>
                                        )}
                                        {eng.status === 'Started' && (
                                            <button
                                                className="btn btn-sm btn-outline-warning"
                                                onClick={() => handleStatusChange(eng.engagementId, 'Closed')}
                                            >
                                                Close Lifecycle
                                            </button>
                                        )}
                                        <button
                                            className={`btn btn-sm ${eng.status === 'Draft' ? 'btn-danger' : 'btn-disabled'}`}
                                            disabled={eng.status !== 'Draft'}
                                            onClick={() => handleDelete(eng)}
                                            title={eng.status !== 'Draft' ? "Physical deletion forbidden once started or closed" : "Delete draft"}
                                        >
                                            🗑️ Delete
                                        </button>
                                    </td>
                                </tr>
                            ))}
                        </tbody>
                    </table>
                </div>
            )}

            {/* Modal */}
            {showModal && (
                <div className="modal-backdrop">
                    <div className="modal-card">
                        <h3>Initiate New Client Engagement</h3>
                        <form onSubmit={handleCreate}>
                            <div className="form-group">
                                <label>Tenant ID:</label>
                                <input type="text" value={tenantId} disabled className="form-input disabled" />
                            </div>
                            <div className="form-group">
                                <label>Client ID:</label>
                                <input
                                    type="text"
                                    value={clientId}
                                    onChange={(e) => setClientId(e.target.value)}
                                    required
                                    className="form-input"
                                />
                            </div>
                            <div className="form-group">
                                <label>Assigned Staff ID:</label>
                                <input
                                    type="text"
                                    value={staffId}
                                    onChange={(e) => setStaffId(e.target.value)}
                                    required
                                    className="form-input"
                                />
                            </div>
                            <div className="modal-actions">
                                <button type="button" className="btn btn-secondary" onClick={() => setShowModal(false)}>
                                    Cancel
                                </button>
                                <button type="submit" className="btn btn-primary" disabled={submitting}>
                                    {submitting ? 'Creating...' : 'Create Engagement'}
                                </button>
                            </div>
                        </form>
                    </div>
                </div>
            )}
        </div>
    );
};
