import React, { useState } from 'react';
import { useAuth } from '../context/AuthContext';
import { ReportsApi, ApiError } from '../services/api';
import { FileBarChart2, Loader2, AlertCircle, Download } from 'lucide-react';

/**
 * Standalone demo/preview for the dynamic report generation feature
 * (Custodian.Shared.Reporting + GET /api/reports/engagements). Deliberately independent of
 * any specific engagement for now — the underlying report is tenant-wide, not per-engagement
 * — but self-contained enough to be embedded elsewhere (e.g. an "Export" button on
 * EngagementsPage) once there's a concrete place it should live permanently.
 */
export const ReportsView: React.FC = () => {
    const { tenantId, role } = useAuth();
    const [format, setFormat] = useState<'csv' | 'json'>('csv');
    const [content, setContent] = useState<string | null>(null);
    const [isLoading, setIsLoading] = useState(false);
    const [error, setError] = useState<string | null>(null);

    const handleGenerate = async () => {
        if (!tenantId) {
            setError('No tenant context available for the current session.');
            return;
        }

        setIsLoading(true);
        setError(null);
        setContent(null);

        try {
            const data = await ReportsApi.getEngagementReport(tenantId, format);
            // JSON comes back already parsed by the shared request() helper; CSV comes back
            // as the raw response text. Either way, render it as text.
            setContent(format === 'json' ? JSON.stringify(data, null, 2) : String(data));
        } catch (err) {
            if (err instanceof ApiError && err.status === 403) {
                setError("You don't have permission to generate this report (Owner/Staff only).");
            } else if (err instanceof Error) {
                setError(err.message);
            } else {
                setError('Failed to generate the report. Please try again.');
            }
        } finally {
            setIsLoading(false);
        }
    };

    return (
        <div className="bg-white/90 backdrop-blur-md p-6 rounded-2xl border border-slate-200/90 shadow-xs space-y-5">
            <div className="flex items-center gap-3">
                <div className="w-10 h-10 rounded-xl bg-indigo-50 border border-indigo-100 flex items-center justify-center text-indigo-600">
                    <FileBarChart2 className="w-5 h-5" />
                </div>
                <div>
                    <h2 className="text-base font-bold text-slate-900">Engagement Report</h2>
                    <p className="text-xs text-slate-500">
                        Every engagement in your workspace ({tenantId || 'unknown tenant'}), generated live from the database.
                    </p>
                </div>
            </div>

            <div className="flex items-center gap-3">
                <div className="flex items-center gap-1 bg-slate-100 p-1 rounded-xl text-xs">
                    <button
                        type="button"
                        onClick={() => setFormat('csv')}
                        className={`px-3 py-1.5 rounded-lg font-semibold transition ${
                            format === 'csv' ? 'bg-white text-indigo-700 shadow-xs' : 'text-slate-600 hover:text-slate-900'
                        }`}
                    >
                        CSV
                    </button>
                    <button
                        type="button"
                        onClick={() => setFormat('json')}
                        className={`px-3 py-1.5 rounded-lg font-semibold transition ${
                            format === 'json' ? 'bg-white text-indigo-700 shadow-xs' : 'text-slate-600 hover:text-slate-900'
                        }`}
                    >
                        JSON
                    </button>
                </div>

                <button
                    type="button"
                    onClick={handleGenerate}
                    disabled={isLoading || role === 'Client'}
                    className="px-4 py-2 rounded-xl bg-gradient-to-r from-[#635bff] to-[#712ae2] hover:opacity-95 text-white text-xs font-bold flex items-center gap-2 shadow-sm shadow-indigo-500/25 transition disabled:opacity-50 disabled:cursor-not-allowed"
                >
                    {isLoading ? (
                        <>
                            <Loader2 className="w-4 h-4 animate-spin" />
                            <span>Generating...</span>
                        </>
                    ) : (
                        <>
                            <Download className="w-4 h-4" />
                            <span>Generate Report</span>
                        </>
                    )}
                </button>
            </div>

            {role === 'Client' && (
                <p className="text-[11px] text-amber-700 bg-amber-50 border border-amber-200 rounded-lg px-3 py-2">
                    This report is restricted to Owner/Staff accounts.
                </p>
            )}

            {error && (
                <div className="p-3 bg-rose-50 border border-rose-200 rounded-xl text-rose-800 text-xs flex items-start gap-2">
                    <AlertCircle className="w-4 h-4 text-rose-600 shrink-0 mt-0.5" />
                    <span>{error}</span>
                </div>
            )}

            {content !== null && (
                <div>
                    <div className="flex items-center justify-between mb-1.5">
                        <span className="text-[10px] font-bold text-slate-400 uppercase tracking-wider">
                            {format.toUpperCase()} Output
                        </span>
                        <span className="text-[10px] text-slate-400">{content.length.toLocaleString()} characters</span>
                    </div>
                    <pre className="w-full max-h-[28rem] overflow-auto p-4 rounded-xl bg-slate-900 text-slate-100 text-[11px] leading-relaxed whitespace-pre-wrap break-words">
                        {content}
                    </pre>
                </div>
            )}
        </div>
    );
};
