import React, { useEffect, useMemo, useState } from 'react';
import { CheckCircle2, Info, RotateCcw, ShieldCheck } from 'lucide-react';
import { useAuth } from '../../context/AuthContext';
import { ClientProfile, Engagement } from '../../types';
import { IdentityApi, ReportsApi, WorkflowApi } from '../../services/api';
import { ReportDownloadButton } from './ReportDownloadButton';
import { engagementOptionLabel, engagementStatusLabel, stageLabel } from '../../utils/displayLabels';


/**
 * CSTD-249: Proof-of-Delivery Report Section.
 * Generates an authorized, tenant-scoped cryptographic proof-of-delivery PDF report
 * backed by SHA-256 hash-chain verification and Workflow access control.
 */
export const ProofOfDeliveryReportSection: React.FC = () => {
    const { tenantId, token } = useAuth();

    const [engagements, setEngagements] = useState<Engagement[]>([]);
    const [clients, setClients] = useState<ClientProfile[]>([]);
    const [isLoadingEngagements, setIsLoadingEngagements] = useState(false);
    const [loadError, setLoadError] = useState<string | null>(null);

    const [selectedEngagementId, setSelectedEngagementId] = useState('');
    const [lastDownload, setLastDownload] = useState<string | null>(null);

    useEffect(() => {
        if (!tenantId) return;
        setIsLoadingEngagements(true);
        setLoadError(null);

        Promise.all([
            WorkflowApi.getEngagements(tenantId).catch(() => [] as Engagement[]),
            IdentityApi.getClients(token || undefined).catch(() => [] as ClientProfile[]),
        ])
            .then(([fetchedEngagements, fetchedClients]) => {
                setEngagements(fetchedEngagements);
                setClients(fetchedClients);
            })
            .catch(() => {
                setLoadError('Unable to load accessible engagements. Please refresh or try again later.');
            })
            .finally(() => {
                setIsLoadingEngagements(false);
            });
    }, [tenantId, token]);

    const engagementOptions = useMemo(
        () =>
            engagements
                .map((e) => {
                    const client = clients.find((c) => c.id === e.clientId);
                    const clientLabel = client?.name ?? 'Unnamed client';
                    return {
                        value: e.engagementId,
                        label: engagementOptionLabel(e, clientLabel),
                    };
                })
                .sort((a, b) => a.label.localeCompare(b.label)),
        [engagements, clients]
    );

    const selectedEngagement = useMemo(
        () => engagements.find((e) => e.engagementId === selectedEngagementId),
        [engagements, selectedEngagementId]
    );

    const resetSelection = () => {
        setSelectedEngagementId('');
    };

    const fieldClass = 'w-full px-3 py-2 rounded-lg border border-slate-200 bg-white text-sm text-slate-700';
    const labelClass = 'block text-[11px] font-bold text-slate-500 uppercase tracking-wider mb-1.5';

    return (
        <section className="bg-white/85 backdrop-blur-md p-6 rounded-2xl border border-slate-200/80 shadow-xs space-y-5">
            <div className="flex items-start justify-between gap-4">
                <div className="flex items-start gap-3">
                    <div className="w-10 h-10 rounded-xl bg-indigo-50 border border-indigo-100 flex items-center justify-center text-indigo-600 flex-shrink-0">
                        <ShieldCheck className="w-5 h-5" />
                    </div>
                    <div>
                        <h2 className="text-lg font-bold text-slate-900">Proof of Delivery</h2>
                        <p className="text-sm text-slate-500">
                            Cryptographic SHA-256 tamper-evident integrity verification, historical audit timeline,
                            and authorized proof-of-delivery report for an engagement.
                        </p>
                    </div>
                </div>
                {selectedEngagementId && (
                    <button
                        type="button"
                        onClick={resetSelection}
                        className="px-3 py-1.5 rounded-lg border border-slate-200 bg-white hover:bg-slate-50 text-slate-600 text-xs font-semibold flex items-center gap-1.5 whitespace-nowrap transition"
                    >
                        <RotateCcw className="w-3.5 h-3.5" />
                        Clear selection
                    </button>
                )}
            </div>

            <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-4">
                <div className="sm:col-span-2 lg:col-span-2">
                    <label htmlFor="pod-engagement" className={labelClass}>
                        Engagement <span className="text-rose-500">*</span>
                    </label>
                    <select
                        id="pod-engagement"
                        value={selectedEngagementId}
                        onChange={(e) => setSelectedEngagementId(e.target.value)}
                        disabled={isLoadingEngagements}
                        className={fieldClass}
                    >
                        <option value="">
                            {isLoadingEngagements
                                ? 'Loading engagements…'
                                : engagementOptions.length === 0
                                ? 'No accessible engagements found'
                                : 'Select an engagement to verify…'}
                        </option>
                        {engagementOptions.map((o) => (
                            <option key={o.value} value={o.value}>
                                {o.label}
                            </option>
                        ))}
                    </select>
                </div>
            </div>

            {loadError && (
                <p role="alert" className="text-xs font-medium text-rose-700">
                    {loadError}
                </p>
            )}

            {!selectedEngagementId && !isLoadingEngagements && (
                <p className="text-xs text-slate-500 italic">
                    Select an accessible engagement from the dropdown above to generate its proof-of-delivery PDF report.
                </p>
            )}

            {selectedEngagement && (
                <div className="p-3 bg-slate-50/80 rounded-xl border border-slate-200/60 text-xs text-slate-600 grid grid-cols-2 sm:grid-cols-4 gap-2">
                    <div>
                        <span className="font-semibold text-slate-500 block">Stage:</span>
                        <span>{stageLabel(selectedEngagement.stage)}</span>
                    </div>
                    <div>
                        <span className="font-semibold text-slate-500 block">Status:</span>
                        <span>{engagementStatusLabel(selectedEngagement.status)}</span>
                    </div>
                    <div>
                        <span className="font-semibold text-slate-500 block">Progress:</span>
                        <span>{selectedEngagement.stageProgressPercentage}%</span>
                    </div>
                    <div>
                        <span className="font-semibold text-slate-500 block">Created:</span>
                        <span>{selectedEngagement.createdAt ? new Date(selectedEngagement.createdAt).toLocaleDateString() : '—'}</span>
                    </div>
                </div>
            )}

            <div className="flex flex-col sm:flex-row sm:items-start justify-between gap-4 pt-4 border-t border-slate-100">
                <p className="flex items-start gap-1.5 text-xs text-slate-500 max-w-xl">
                    <Info className="w-3.5 h-3.5 mt-px flex-shrink-0" />
                    Generates a downloadable tamper-evident audit report containing live SHA-256 hash-chain verification results,
                    the complete historical audit timeline, abbreviated cryptographic hashes, and exact ledger boundaries.
                    Available to Owners and assigned Staff members.
                </p>
                <div className="flex flex-wrap items-start gap-2">
                    <ReportDownloadButton
                        url={ReportsApi.proofOfDeliveryUrl()}
                        format="pdf"
                        params={{ engagementId: selectedEngagementId }}
                        disabled={!selectedEngagementId}
                        label="Download Proof of Delivery (PDF)"
                        onDownloaded={setLastDownload}
                    />
                </div>
            </div>

            {lastDownload && (
                <p className="flex items-center gap-1.5 text-xs text-emerald-700">
                    <CheckCircle2 className="w-3.5 h-3.5" />
                    Downloaded {lastDownload}
                </p>
            )}
        </section>
    );
};

export default ProofOfDeliveryReportSection;
