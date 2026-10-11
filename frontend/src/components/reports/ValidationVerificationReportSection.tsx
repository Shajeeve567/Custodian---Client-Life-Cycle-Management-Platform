import React, { useEffect, useMemo, useState } from 'react';
import { CalendarRange, CheckCircle2, FileCheck, Info, RotateCcw } from 'lucide-react';
import { useAuth } from '../../context/AuthContext';
import { ClientProfile, Engagement } from '../../types';
import { IdentityApi, ReportsApi, WorkflowApi } from '../../services/api';
import { ReportDownloadButton } from './ReportDownloadButton';

/** yyyy-MM-dd of a UTC day, the format the report API takes. */
const utcDate = (date: Date) => date.toISOString().slice(0, 10);

const shortId = (id: string) => id.replace(/-/g, '').slice(0, 6).toUpperCase();

/**
 * CSTD-182 / CSTD-31: Validation & Verification Report Section.
 * Generates live PDF / CSV reports for document automatic compliance validation
 * and human verification metrics across the tenant or assigned engagements.
 */
export const ValidationVerificationReportSection: React.FC = () => {
    const { tenantId, token } = useAuth();

    const [engagements, setEngagements] = useState<Engagement[]>([]);
    const [clients, setClients] = useState<ClientProfile[]>([]);

    const [from, setFrom] = useState('');
    const [to, setTo] = useState('');
    const [engagementId, setEngagementId] = useState('');
    const [lastDownload, setLastDownload] = useState<string | null>(null);

    useEffect(() => {
        if (!tenantId) return;
        // Workflow returns engagements filtered by caller role (all for Owner, assigned for Staff)
        WorkflowApi.getEngagements(tenantId).then(setEngagements).catch(() => setEngagements([]));
        IdentityApi.getClients(token || undefined).then(setClients).catch(() => setClients([]));
    }, [tenantId, token]);

    const engagementOptions = useMemo(
        () =>
            engagements
                .map((e) => {
                    const client = clients.find((c) => c.id === e.clientId);
                    return {
                        value: e.engagementId,
                        label: `${client?.name ?? `Client ${shortId(e.clientId)}`} · ENG-${shortId(e.engagementId)}`,
                    };
                })
                .sort((a, b) => a.label.localeCompare(b.label)),
        [engagements, clients]
    );

    const rangeError = useMemo(() => {
        if (from && to && from > to) {
            return '"From" must be on or before "To".';
        }
        return null;
    }, [from, to]);

    const params = {
        from: from || undefined,
        to: to || undefined,
        engagementId: engagementId || undefined,
    };

    const resetFilters = () => {
        setFrom('');
        setTo('');
        setEngagementId('');
    };

    const fieldClass = 'w-full px-3 py-2 rounded-lg border border-slate-200 bg-white text-sm text-slate-700';
    const labelClass = 'block text-[11px] font-bold text-slate-500 uppercase tracking-wider mb-1.5';

    return (
        <section className="bg-white/85 backdrop-blur-md p-6 rounded-2xl border border-slate-200/80 shadow-xs space-y-5">
            <div className="flex items-start justify-between gap-4">
                <div className="flex items-start gap-3">
                    <div className="w-10 h-10 rounded-xl bg-emerald-50 border border-emerald-100 flex items-center justify-center text-emerald-600 flex-shrink-0">
                        <FileCheck className="w-5 h-5" />
                    </div>
                    <div>
                        <h2 className="text-lg font-bold text-slate-900">Validation &amp; Verification</h2>
                        <p className="text-sm text-slate-500">
                            Automatic compliance status, human verification progress, document type distribution,
                            and detailed rejection reasons across active workspace documents.
                        </p>
                    </div>
                </div>
                <button
                    type="button"
                    onClick={resetFilters}
                    className="px-3 py-1.5 rounded-lg border border-slate-200 bg-white hover:bg-slate-50 text-slate-600 text-xs font-semibold flex items-center gap-1.5 whitespace-nowrap transition"
                >
                    <RotateCcw className="w-3.5 h-3.5" />
                    Reset filters
                </button>
            </div>

            <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-4">
                <div>
                    <label htmlFor="val-from" className={labelClass}>From (UTC, upload date)</label>
                    <input
                        id="val-from"
                        type="date"
                        value={from}
                        max={to || undefined}
                        onChange={(e) => setFrom(e.target.value)}
                        className={fieldClass}
                    />
                </div>
                <div>
                    <label htmlFor="val-to" className={labelClass}>To (UTC, inclusive)</label>
                    <input
                        id="val-to"
                        type="date"
                        value={to}
                        min={from || undefined}
                        onChange={(e) => setTo(e.target.value)}
                        className={fieldClass}
                    />
                </div>
                <div>
                    <label htmlFor="val-engagement" className={labelClass}>Engagement</label>
                    <select
                        id="val-engagement"
                        value={engagementId}
                        onChange={(e) => setEngagementId(e.target.value)}
                        className={fieldClass}
                    >
                        <option value="">All engagements</option>
                        {engagementOptions.map((o) => (
                            <option key={o.value} value={o.value}>{o.label}</option>
                        ))}
                    </select>
                </div>
            </div>

            {rangeError && (
                <p role="alert" className="flex items-center gap-1.5 text-xs font-medium text-rose-700">
                    <CalendarRange className="w-3.5 h-3.5" />
                    {rangeError}
                </p>
            )}

            <div className="flex flex-col sm:flex-row sm:items-start justify-between gap-4 pt-4 border-t border-slate-100">
                <p className="flex items-start gap-1.5 text-xs text-slate-500 max-w-xl">
                    <Info className="w-3.5 h-3.5 mt-px flex-shrink-0" />
                    Aggregates active uploaded documents matching selected date boundaries and engagement scope.
                    Automatic validation and human verification are reported separately. Soft-deleted documents are excluded.
                </p>
                <div className="flex flex-wrap items-start gap-2">
                    <ReportDownloadButton
                        url={ReportsApi.validationVerificationUrl()}
                        format="pdf"
                        params={params}
                        disabled={!!rangeError}
                        onDownloaded={setLastDownload}
                    />
                    <ReportDownloadButton
                        url={ReportsApi.validationVerificationUrl()}
                        format="csv"
                        params={params}
                        variant="secondary"
                        disabled={!!rangeError}
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

export default ValidationVerificationReportSection;
