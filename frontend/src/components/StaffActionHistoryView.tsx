import React, { useState, useEffect, useCallback } from 'react';
import { ClientActionResponse } from '../types/clientAction';
import './StaffActionHistoryView.css';
import { API_BASE } from '../services/api';

interface StaffActionHistoryViewProps {
    engagementId: string;
    tenantId: string;
    baseUrl?: string;
    isClientViewInitial?: boolean;
}

export const StaffActionHistoryView: React.FC<StaffActionHistoryViewProps> = ({
    engagementId,
    tenantId,
    baseUrl = API_BASE.WORKFLOW,
    isClientViewInitial = false,
}) => {
    const [currentEngagementId, setCurrentEngagementId] = useState<string>(
        engagementId && engagementId !== 'eng-1001' ? engagementId : 'e689ce2c-b694-4860-aa0d-96d946283b71'
    );
    const [actions, setActions] = useState<ClientActionResponse[]>([]);
    const [loading, setLoading] = useState<boolean>(true);
    const [error, setError] = useState<string | null>(null);
    const [statusFilter, setStatusFilter] = useState<string>('All');
    const [isClientView, setIsClientView] = useState<boolean>(isClientViewInitial);
    const [completingId, setCompletingId] = useState<string | null>(null);
    const [staffActorName, setStaffActorName] = useState<string>('Staff Admin');

    const fetchActionHistory = useCallback(async () => {
        if (!currentEngagementId) return;
        setLoading(true);
        setError(null);

        try {
            let url = `${baseUrl}/api/engagements/${currentEngagementId}/actions?isClientView=${isClientView}`;
            if (statusFilter !== 'All') {
                url += `&status=${encodeURIComponent(statusFilter)}`;
            }

            const response = await fetch(url, {
                method: 'GET',
                headers: {
                    'Content-Type': 'application/json',
                    'X-Tenant-ID': tenantId,
                },
            });

            if (!response.ok) {
                const text = await response.text();
                throw new Error(text || `Failed to load action history: HTTP ${response.status}`);
            }

            const data: ClientActionResponse[] = await response.json();
            setActions(data);
        } catch (err: any) {
            setError(err.message || 'An unexpected error occurred while loading action history.');
        } finally {
            setLoading(false);
        }
    }, [currentEngagementId, tenantId, baseUrl, isClientView, statusFilter]);

    useEffect(() => {
        fetchActionHistory();
    }, [fetchActionHistory]);

    const handleCompleteAction = async (actionId: string) => {
        setCompletingId(actionId);
        try {
            const response = await fetch(
                `${baseUrl}/api/engagements/${currentEngagementId}/actions/${actionId}/complete`,
                {
                    method: 'PUT',
                    headers: {
                        'Content-Type': 'application/json',
                        'X-Tenant-ID': tenantId,
                    },
                    body: JSON.stringify({ completedByActor: staffActorName }),
                }
            );

            if (!response.ok) {
                throw new Error(`Failed to complete action: HTTP ${response.status}`);
            }

            await fetchActionHistory();
        } catch (err: any) {
            alert(`Error completing action: ${err.message}`);
        } finally {
            setCompletingId(null);
        }
    };

    const getStatusBadge = (status: string) => {
        switch (status.toLowerCase()) {
            case 'completed':
                return <span className="action-badge badge-completed">✓ Completed</span>;
            case 'pending':
                return <span className="action-badge badge-pending">⏳ Pending</span>;
            case 'overdue':
                return <span className="action-badge badge-overdue">⚠ Overdue</span>;
            default:
                return <span className="action-badge badge-default">{status}</span>;
        }
    };

    const formatMetadata = (jsonString?: string) => {
        if (!jsonString) return null;
        try {
            const parsed = JSON.parse(jsonString);
            return (
                <pre className="metadata-json-preview">
                    {JSON.stringify(parsed, null, 2)}
                </pre>
            );
        } catch {
            return <span className="metadata-text">{jsonString}</span>;
        }
    };

    return (
        <div className="action-history-container">
            {/* Header & Controls Bar */}
            <div className="action-history-header">
                <div>
                    <h2 className="action-history-title">Engagement Action History</h2>
                    <p className="action-history-subtitle">
                        Tracking onboard lifecycle events, client actions, and verification audit trails.
                    </p>
                </div>

                <div className="action-history-controls">
                    {/* Engagement ID Picker */}
                    <div className="control-group">
                        <label htmlFor="engagement-id-input">Engagement ID:</label>
                        <input
                            id="engagement-id-input"
                            type="text"
                            value={currentEngagementId}
                            onChange={(e) => setCurrentEngagementId(e.target.value)}
                            className="filter-select font-mono"
                            placeholder="e.g. GUID"
                        />
                    </div>

                    {/* Status Filter */}
                    <div className="control-group">
                        <label htmlFor="status-filter">Filter Status:</label>
                        <select
                            id="status-filter"
                            value={statusFilter}
                            onChange={(e) => setStatusFilter(e.target.value)}
                            className="filter-select"
                        >
                            <option value="All">All Actions</option>
                            <option value="Pending">Pending</option>
                            <option value="Completed">Completed</option>
                        </select>
                    </div>

                    {/* Perspective Toggle (Staff vs Client-Safe View) */}
                    <div className="control-group">
                        <label htmlFor="view-toggle">Perspective:</label>
                        <button
                            id="view-toggle"
                            type="button"
                            onClick={() => setIsClientView(!isClientView)}
                            className={`toggle-btn ${isClientView ? 'btn-client' : 'btn-staff'}`}
                        >
                            {isClientView ? '👁 Client View (Filtered)' : '🛡 Staff View (Audit Metadata)'}
                        </button>
                    </div>
                </div>
            </div>

            {/* Main Content Area */}
            {loading ? (
                <div className="loading-state">
                    <div className="spinner"></div>
                    <p>Fetching live engagement action history...</p>
                </div>
            ) : error ? (
                <div className="error-state">
                    <p className="error-msg">⚠️ {error}</p>
                    <button type="button" onClick={fetchActionHistory} className="retry-btn">
                        Retry Connection
                    </button>
                </div>
            ) : actions.length === 0 ? (
                <div className="empty-state">
                    <div className="empty-icon">📋</div>
                    <h3>No Actions Found</h3>
                    <p>No engagement actions match the selected filter criteria.</p>
                </div>
            ) : (
                <div className="action-timeline">
                    {actions.map((action) => (
                        <div
                            key={action.actionId}
                            className={`action-card ${action.isInternalOnly ? 'internal-card' : ''
                                } ${action.status === 'Completed' ? 'completed-card' : ''}`}
                        >
                            <div className="action-card-header">
                                <div className="action-title-group">
                                    <span className="action-type-tag">{action.type}</span>
                                    <h3 className="action-item-title">{action.title}</h3>
                                    {action.isInternalOnly && (
                                        <span className="internal-only-badge">🔒 Staff Internal</span>
                                    )}
                                </div>
                                {getStatusBadge(action.status)}
                            </div>

                            {action.description && (
                                <p className="action-description">{action.description}</p>
                            )}

                            {/* Action Meta Info */}
                            <div className="action-meta-footer">
                                <div className="meta-col">
                                    <span className="meta-label">Source Step:</span>
                                    <span className="meta-value">{action.source}</span>
                                </div>
                                <div className="meta-col">
                                    <span className="meta-label">Assigned Role:</span>
                                    <span className="meta-value">{action.assignedToRole}</span>
                                </div>
                                <div className="meta-col">
                                    <span className="meta-label">Created:</span>
                                    <span className="meta-value">
                                        {new Date(action.createdAt).toLocaleString()}
                                    </span>
                                </div>

                                {action.status === 'Completed' && (
                                    <>
                                        <div className="meta-col">
                                            <span className="meta-label">Completed By:</span>
                                            <span className="meta-value">{action.completedByActor || 'System'}</span>
                                        </div>
                                        <div className="meta-col">
                                            <span className="meta-label">Completed Date:</span>
                                            <span className="meta-value">
                                                {action.completedAt
                                                    ? new Date(action.completedAt).toLocaleString()
                                                    : 'N/A'}
                                            </span>
                                        </div>
                                    </>
                                )}
                            </div>

                            {/* Staff Source Metadata Section (Hidden in Client View) */}
                            {!isClientView && action.sourceMetadata && (
                                <div className="staff-metadata-drawer">
                                    <span className="metadata-header-label">🔍 Staff Source Audit Payload:</span>
                                    {formatMetadata(action.sourceMetadata)}
                                </div>
                            )}

                            {/* Action Execution Button for Pending items */}
                            {action.status === 'Pending' && !isClientView && (
                                <div className="action-complete-bar">
                                    <div className="actor-input-group">
                                        <label htmlFor={`actor-${action.actionId}`}>Staff Actor:</label>
                                        <input
                                            id={`actor-${action.actionId}`}
                                            type="text"
                                            value={staffActorName}
                                            onChange={(e) => setStaffActorName(e.target.value)}
                                            className="actor-input"
                                        />
                                    </div>
                                    <button
                                        type="button"
                                        disabled={completingId === action.actionId}
                                        onClick={() => handleCompleteAction(action.actionId)}
                                        className="complete-action-btn"
                                    >
                                        {completingId === action.actionId ? 'Updating...' : '✓ Mark Action Completed'}
                                    </button>
                                </div>
                            )}
                        </div>
                    ))}
                </div>
            )}
        </div>
    );
};

export default StaffActionHistoryView;
