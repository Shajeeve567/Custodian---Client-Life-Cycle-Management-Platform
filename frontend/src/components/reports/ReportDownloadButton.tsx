import React, { useState } from 'react';
import { AlertCircle, Download, FileSpreadsheet, FileText, Loader2 } from 'lucide-react';
import { downloadReport, ReportDownloadError, ReportParams, ReportProblem } from '../../services/api';

export type ReportFormat = 'pdf' | 'csv';

interface ReportDownloadButtonProps {
    /** Report endpoint, e.g. `${API_BASE.WORKFLOW}/api/reports/sla-performance`. */
    url: string;
    format: ReportFormat;
    /** Filters sent as query parameters; empty values are left out. */
    params?: ReportParams;
    label?: string;
    disabled?: boolean;
    /** Primary (filled) or secondary (outlined) look. */
    variant?: 'primary' | 'secondary';
    onDownloaded?: (fileName: string) => void;
}

/**
 * CSTD-36-N2: downloads a report with the user's token. Shows a spinner while the report is generated
 * and, on failure, the server's ProblemDetails title and detail (plus the reference to quote for a 500).
 */
export const ReportDownloadButton: React.FC<ReportDownloadButtonProps> = ({
    url,
    format,
    params,
    label,
    disabled = false,
    variant = 'primary',
    onDownloaded,
}) => {
    const [isLoading, setIsLoading] = useState(false);
    const [problem, setProblem] = useState<ReportProblem | null>(null);

    const handleClick = async () => {
        setIsLoading(true);
        setProblem(null);
        try {
            const { fileName } = await downloadReport(url, { ...params, format });
            onDownloaded?.(fileName);
        } catch (err) {
            setProblem(
                err instanceof ReportDownloadError
                    ? err.problem
                    : { status: 0, title: 'Report could not be generated', detail: err instanceof Error ? err.message : undefined }
            );
        } finally {
            setIsLoading(false);
        }
    };

    const Icon = isLoading ? Loader2 : format === 'csv' ? FileSpreadsheet : FileText;
    const text = label ?? `Download ${format.toUpperCase()}`;
    const base =
        'inline-flex items-center justify-center gap-1.5 px-3.5 py-2 rounded-lg text-xs font-bold transition shadow-xs disabled:opacity-60 disabled:cursor-not-allowed';
    const look =
        variant === 'primary'
            ? 'bg-indigo-600 hover:bg-indigo-700 text-white'
            : 'bg-white hover:bg-slate-50 text-slate-700 border border-slate-200';

    return (
        <div className="flex flex-col gap-1.5">
            <button
                type="button"
                onClick={handleClick}
                disabled={disabled || isLoading}
                aria-busy={isLoading}
                className={`${base} ${look}`}
            >
                <Icon className={`w-3.5 h-3.5 ${isLoading ? 'animate-spin' : ''}`} />
                <span>{isLoading ? 'Generating…' : text}</span>
                {!isLoading && <Download className="w-3 h-3 opacity-70" />}
            </button>

            {problem && (
                <div role="alert" className="flex items-start gap-1.5 p-2 rounded-lg bg-rose-50 border border-rose-200 text-[11px] text-rose-800">
                    <AlertCircle className="w-3.5 h-3.5 mt-px flex-shrink-0 text-rose-600" />
                    <div className="space-y-0.5">
                        <p className="font-semibold">{problem.title}</p>
                        {problem.detail && <p>{problem.detail}</p>}
                        {problem.correlationId && problem.status >= 500 && (
                            <p className="text-rose-600/80">Reference: {problem.correlationId}</p>
                        )}
                    </div>
                </div>
            )}
        </div>
    );
};

export default ReportDownloadButton;
