import React, { useState, useEffect } from 'react';
import { useAuth } from '../context/AuthContext';
import { AuditEvent } from '../types';
import { AuditApi } from '../services/api';

export const AuditLogView: React.FC = () => {
    const { tenantId } = useAuth();
    const [events, setEvents] = useState<AuditEvent[]>([]);
    const [loading, setLoading] = useState<boolean>(true);
    const [error, setError] = useState<string | null>(null);
    const [verifying, setVerifying] = useState<boolean>(false);
    const [verificationResult, setVerificationResult] = useState<{ isVerified: boolean; count: number } | null>(null);

    const fetchEvents = async () => {
        setLoading(true);
        setError(null);
        try {
            const data = await AuditApi.getEvents(tenantId);
            setEvents(data);
        } catch (err: any) {
            setError(err.message || 'Failed to connect to Audit Microservice');
        } finally {
            setLoading(false);
        }
    };

    useEffect(() => {
        fetchEvents();
    }, [tenantId]);

    const handleVerifyChain = async () => {
        setVerifying(true);
        try {
            const result = await AuditApi.verifyChain(tenantId);
            setVerificationResult(result);
        } catch (err: any) {
            alert('Verification Error: ' + err.message);
        } finally {
            setVerifying(false);
        }
    };

    return (
        <div className="section-container">
            <div className="section-header">
                <div>
                    <h2>Genesis Immutable Audit Trail</h2>
                    <p className="section-desc">Append-only, tamper-evident cryptographic event log using SHA-256 hash chaining.</p>
                </div>
                <button
                    className="btn btn-success"
                    onClick={handleVerifyChain}
                    disabled={verifying}
                >
                    {verifying ? 'Verifying Hashes...' : '🔐 Verify Cryptographic Chain'}
                </button>
            </div>

            {verificationResult && (
                <div className={`alert ${verificationResult.isVerified ? 'alert-success' : 'alert-danger'} mb-4`}>
                    <strong>{verificationResult.isVerified ? '✅ Hash Chain Verified Intact!' : '❌ Cryptographic Tampering Detected!'}</strong>
                    <p className="text-sm mt-1">Verified {verificationResult.count} sequential audit events for tenant <strong>{tenantId}</strong>.</p>
                </div>
            )}

            {error && (
                <div className="alert alert-warning">
                    <strong>Notice:</strong> {error}. Showing demonstration log state.
                </div>
            )}

            {loading ? (
                <div className="loading-skeleton">Loading genesis audit trail...</div>
            ) : events.length === 0 ? (
                <div className="empty-card">
                    <div className="empty-icon">📜</div>
                    <h3>No Audit Events Logged</h3>
                    <p>No audit events recorded for <strong>{tenantId}</strong>.</p>
                </div>
            ) : (
                <div className="table-wrapper">
                    <table className="data-table">
                        <thead>
                            <tr>
                                <th>Seq #</th>
                                <th>Event Type</th>
                                <th>Actor</th>
                                <th>Engagement ID</th>
                                <th>Timestamp</th>
                                <th>SHA-256 Hash</th>
                            </tr>
                        </thead>
                        <tbody>
                            {events.map((evt) => (
                                <tr key={evt.eventId}>
                                    <td className="font-mono text-center font-bold">#{evt.sequenceNumber}</td>
                                    <td>
                                        <span className={`badge ${evt.type.includes('Genesis') ? 'badge-genesis' : 'badge-type'}`}>
                                            {evt.type}
                                        </span>
                                    </td>
                                    <td>{evt.actor}</td>
                                    <td className="font-mono text-xs">{evt.engagementId}</td>
                                    <td className="text-xs text-muted">{new Date(evt.timestamp).toLocaleString()}</td>
                                    <td className="font-mono text-xs hash-cell" title={evt.hash}>
                                        {evt.hash.substring(0, 16)}...
                                    </td>
                                </tr>
                            ))}
                        </tbody>
                    </table>
                </div>
            )}
        </div>
    );
};
