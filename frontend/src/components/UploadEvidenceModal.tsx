import React, { useState } from 'react';
import { ClientSafeAction } from '../types';
import { DocumentsApi, WorkflowApi } from '../services/api';
import {
    X,
    UploadCloud,
    FileText,
    AlertCircle,
    CheckCircle2,
    Loader2,
    Shield
} from 'lucide-react';

interface UploadEvidenceModalProps {
    isOpen: boolean;
    onClose: () => void;
    action: ClientSafeAction | null;
    engagementId: string;
    tenantId: string;
    userId?: string | null;
    onSuccess: () => void;
}

export const UploadEvidenceModal: React.FC<UploadEvidenceModalProps> = ({
    isOpen,
    onClose,
    action,
    engagementId,
    tenantId,
    userId,
    onSuccess,
}) => {
    const [file, setFile] = useState<File | null>(null);
    const [isUploading, setIsUploading] = useState<boolean>(false);
    const [error, setError] = useState<string | null>(null);
    const [isDragOver, setIsDragOver] = useState<boolean>(false);

    if (!isOpen || !action) return null;

    const handleFileValidation = (selectedFile: File) => {
        setError(null);

        // Security requirement: Only PDF documents accepted
        const isPdf = selectedFile.name.toLowerCase().endsWith('.pdf') || selectedFile.type === 'application/pdf';
        if (!isPdf) {
            setError('Compliance policy requires valid PDF (.pdf) format only.');
            return false;
        }

        // Maximum file size limit: 5MB
        const maxSizeBytes = 5 * 1024 * 1024;
        if (selectedFile.size > maxSizeBytes) {
            setError('File size exceeds the 5MB maximum limit. Please upload an optimized PDF.');
            return false;
        }

        setFile(selectedFile);
        return true;
    };

    const handleFileInputChange = (e: React.ChangeEvent<HTMLInputElement>) => {
        if (e.target.files && e.target.files[0]) {
            handleFileValidation(e.target.files[0]);
        }
    };

    const handleDrop = (e: React.DragEvent<HTMLDivElement>) => {
        e.preventDefault();
        setIsDragOver(false);
        if (e.dataTransfer.files && e.dataTransfer.files[0]) {
            handleFileValidation(e.dataTransfer.files[0]);
        }
    };

    const handleDragOver = (e: React.DragEvent<HTMLDivElement>) => {
        e.preventDefault();
        setIsDragOver(true);
    };

    const handleDragLeave = (e: React.DragEvent<HTMLDivElement>) => {
        e.preventDefault();
        setIsDragOver(false);
    };

    const handleSubmit = async (e: React.FormEvent) => {
        e.preventDefault();
        if (!file || !engagementId || !tenantId) return;

        setIsUploading(true);
        setError(null);

        try {
            // 1. Upload binary PDF to Document Vault Microservice
            const formData = new FormData();
            formData.append('File', file);
            formData.append('Type', action.type || 'ComplianceEvidence');
            formData.append('UploaderId', userId || 'client-user');

            const uploadedDoc = await DocumentsApi.uploadDocument(engagementId, formData, tenantId);

            // 2. Automatically transition Workflow action status from Pending/Rejected -> Uploaded
            await WorkflowApi.uploadEvidence(
                engagementId,
                action.actionId,
                {
                    uploaderActor: userId || 'client-user',
                    documentId: uploadedDoc?.documentId,
                },
                tenantId
            );

            // Reset and trigger live portal reload
            setFile(null);
            onSuccess();
            onClose();
        } catch (err: any) {
            setError(err.message || 'Failed to submit document evidence. Please try again.');
        } finally {
            setIsUploading(false);
        }
    };

    return (
        <div className="fixed inset-0 z-50 flex items-center justify-center p-4 bg-slate-900/60 backdrop-blur-xs animate-in fade-in duration-200">
            <div className="bg-white rounded-2xl shadow-xl border border-slate-200 w-full max-w-lg overflow-hidden">
                {/* Header */}
                <div className="p-5 border-b border-slate-100 flex items-center justify-between">
                    <div className="flex items-center gap-3">
                        <div className="w-10 h-10 rounded-xl bg-indigo-50 border border-indigo-100 flex items-center justify-center text-indigo-600">
                            <UploadCloud className="w-5 h-5" />
                        </div>
                        <div>
                            <h3 className="text-base font-bold text-slate-900">Upload Evidence Document</h3>
                            <p className="text-xs text-slate-500">Stage {action.stageNumber} • {action.title}</p>
                        </div>
                    </div>
                    <button
                        onClick={onClose}
                        disabled={isUploading}
                        className="p-1.5 rounded-lg text-slate-400 hover:text-slate-600 hover:bg-slate-100 transition"
                    >
                        <X className="w-5 h-5" />
                    </button>
                </div>

                {/* Form Body */}
                <form onSubmit={handleSubmit} className="p-6 space-y-4">
                    {error && (
                        <div className="p-3 bg-rose-50 border border-rose-200 rounded-xl text-rose-800 text-xs flex items-start gap-2">
                            <AlertCircle className="w-4 h-4 text-rose-600 shrink-0 mt-0.5" />
                            <span>{error}</span>
                        </div>
                    )}

                    {/* Drag and drop box */}
                    <div
                        onDrop={handleDrop}
                        onDragOver={handleDragOver}
                        onDragLeave={handleDragLeave}
                        className={`border-2 border-dashed rounded-2xl p-6 text-center transition cursor-pointer ${
                            isDragOver
                                ? 'border-indigo-500 bg-indigo-50/50'
                                : file
                                ? 'border-emerald-300 bg-emerald-50/30'
                                : 'border-slate-200 hover:border-indigo-300 hover:bg-slate-50'
                        }`}
                        onClick={() => document.getElementById('evidence-file-input')?.click()}
                    >
                        <input
                            id="evidence-file-input"
                            type="file"
                            accept=".pdf,application/pdf"
                            onChange={handleFileInputChange}
                            className="hidden"
                        />

                        {file ? (
                            <div className="space-y-2">
                                <div className="w-12 h-12 rounded-xl bg-emerald-100 text-emerald-600 flex items-center justify-center mx-auto">
                                    <FileText className="w-6 h-6" />
                                </div>
                                <div className="text-xs font-bold text-slate-900 truncate max-w-xs mx-auto">
                                    {file.name}
                                </div>
                                <div className="text-[11px] text-slate-500">
                                    {(file.size / (1024 * 1024)).toFixed(2)} MB • Ready for Document Vault
                                </div>
                                <button
                                    type="button"
                                    onClick={(e) => {
                                        e.stopPropagation();
                                        setFile(null);
                                    }}
                                    className="text-[11px] font-semibold text-rose-600 hover:underline pt-1"
                                >
                                    Choose another file
                                </button>
                            </div>
                        ) : (
                            <div className="space-y-2">
                                <div className="w-12 h-12 rounded-xl bg-indigo-50 text-indigo-600 flex items-center justify-center mx-auto">
                                    <UploadCloud className="w-6 h-6" />
                                </div>
                                <div className="text-xs font-semibold text-slate-800">
                                    Drag and drop your PDF here, or <span className="text-indigo-600 underline">browse</span>
                                </div>
                                <p className="text-[11px] text-slate-400">
                                    Required format: PDF document up to 5MB
                                </p>
                            </div>
                        )}
                    </div>

                    {/* Informational note */}
                    <div className="p-3 bg-slate-50 rounded-xl border border-slate-200 flex items-center gap-2 text-xs text-slate-600">
                        <Shield className="w-4 h-4 text-indigo-600 shrink-0" />
                        <span>
                            Upon upload, this task transitions to <strong className="text-slate-800">Under Review</strong> for custodian verification.
                        </span>
                    </div>

                    {/* Modal Footer */}
                    <div className="pt-2 flex items-center justify-end gap-2.5">
                        <button
                            type="button"
                            onClick={onClose}
                            disabled={isUploading}
                            className="px-4 py-2 rounded-xl border border-slate-200 text-xs font-semibold text-slate-600 hover:bg-slate-50 transition"
                        >
                            Cancel
                        </button>
                        <button
                            type="submit"
                            disabled={!file || isUploading}
                            className={`px-5 py-2 rounded-xl text-xs font-bold text-white flex items-center gap-2 transition shadow-sm ${
                                file && !isUploading
                                    ? 'bg-gradient-to-r from-[#635bff] to-[#712ae2] hover:opacity-95 shadow-indigo-500/25 cursor-pointer'
                                    : 'bg-slate-200 text-slate-400 cursor-not-allowed'
                            }`}
                        >
                            {isUploading ? (
                                <>
                                    <Loader2 className="w-4 h-4 animate-spin" />
                                    <span>Uploading & Submitting...</span>
                                </>
                            ) : (
                                <>
                                    <CheckCircle2 className="w-4 h-4" />
                                    <span>Submit Evidence</span>
                                </>
                            )}
                        </button>
                    </div>
                </form>
            </div>
        </div>
    );
};
