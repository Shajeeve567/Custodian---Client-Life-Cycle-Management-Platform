import React, { useState, useEffect } from 'react';
import { useAuth } from '../context/AuthContext';
import { AuditEvent, ChainVerificationResult } from '../types';
import { AuditApi } from '../services/api';

export const AuditLogView: React.FC = () => {
    const { tenantId } = useAuth();
    const [events, setEvents] = useState<AuditEvent[]>([]);
    const [loading, setLoading] = useState<boolean>(true);
    const [error, setError] = useState<string | null>(null);
    const [verifying, setVerifying] = useState<boolean>(false);
    const [verificationResult, setVerificationResult] = useState<{
        isVerified: boolean;
        count: number;
        engagementsChecked: number;
        failures: ChainVerificationResult[];
    } | null>(null);

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

    // Each engagement has its own hash chain: verify every engagement that appears in the log.
    const handleVerifyChain = async () => {
        setVerifying(true);
        try {
            const engagementIds = Array.from(new Set(events.map((e) => e.engagementId)));
            const results = await Promise.all(engagementIds.map((id) => AuditApi.verifyChain(id, tenantId)));
            const failures = results.filter((r) => !r.isVerified);
            setVerificationResult({
                isVerified: failures.length === 0,
                count: results.reduce((sum, r) => sum + r.count, 0),
                engagementsChecked: results.length,
                failures,
            });
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
                    disabled={verifying || events.length === 0}
                >
                    {verifying ? 'Verifying Hashes...' : '🔐 Verify Cryptographic Chain'}
                </button>
            </div>

            {verificationResult && (
                <div className={`alert ${verificationResult.isVerified ? 'alert-success' : 'alert-danger'} mb-4`}>
                    <strong>{verificationResult.isVerified ? '✅ Hash Chain Verified Intact!' : '❌ Cryptographic Tampering Detected!'}</strong>
                    <p className="text-sm mt-1">
                        Checked {verificationResult.count} audit events across {verificationResult.engagementsChecked} engagement chain
                        {verificationResult.engagementsChecked === 1 ? '' : 's'}.
                    </p>
                    {verificationResult.failures.map((f) => (
                        <p key={f.engagementId ?? f.brokenAtEventId} className="text-sm mt-1 font-mono">
                            Engagement {f.engagementId}: {f.reason} (event {f.brokenAtEventId})
                        </p>
                    ))}
                </div>
            )}

            {error && (
                <div className="alert alert-warning">
                    <strong>Could not load the audit log:</strong> {error}
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
