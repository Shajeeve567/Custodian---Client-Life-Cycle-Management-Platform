import React, { useState, useEffect, useMemo } from 'react';
import { useAuth } from '../context/AuthContext';
import { DocumentMetadata, Engagement, ClientAction } from '../types';
import { DocumentsApi, WorkflowApi } from '../services/api';
import {
    FileText,
    ShieldCheck,
    ShieldAlert,
    CheckCircle2,
    XCircle,
    Clock,
    Download,
    Trash2,
    Edit3,
    Filter,
    RefreshCw,
    AlertTriangle,
    Info,
    Check,
    X,
    UploadCloud,
    Search,
    Building,
    Lock,
    FileCheck
} from 'lucide-react';

export const DocumentVaultView: React.FC = () => {
    const { tenantId, userId } = useAuth();

    // Engagement selection
    const [engagements, setEngagements] = useState<Engagement[]>([]);
    const [selectedEngagementId, setSelectedEngagementId] = useState<string>('e689ce2c-b694-4860-aa0d-96d946283b71');
    const [isCustomEngagement, setIsCustomEngagement] = useState<boolean>(false);
    const [customEngagementInput, setCustomEngagementInput] = useState<string>('');

    // Document state
    const [documents, setDocuments] = useState<DocumentMetadata[]>([]);
    const [loading, setLoading] = useState<boolean>(true);
    const [error, setError] = useState<string | null>(null);
    const [successBanner, setSuccessBanner] = useState<string | null>(null);

    // Filter controls
    const [filterType, setFilterType] = useState<string>('ALL');
    const [filterCompliance, setFilterCompliance] = useState<string>('ALL');
    const [filterVerification, setFilterVerification] = useState<string>('ALL');
    const [includeDeleted, setIncludeDeleted] = useState<boolean>(false);
    const [searchQuery, setSearchQuery] = useState<string>('');

    // Upload section toggle & form
    const [isUploadDrawerOpen, setIsUploadDrawerOpen] = useState<boolean>(false);
    const [uploadFile, setUploadFile] = useState<File | null>(null);
    const [uploadType, setUploadType] = useState<string>('KYC_PASSPORT');
    const [issueDate, setIssueDate] = useState<string>(new Date().toISOString().split('T')[0]);
    const [expiryDate, setExpiryDate] = useState<string>(
        new Date(Date.now() + 365 * 24 * 60 * 60 * 1000).toISOString().split('T')[0]
    );
    const [uploading, setUploading] = useState<boolean>(false);
    const [uploadError, setUploadError] = useState<string | null>(null);

    // Action Modals State
    const [verifyingDoc, setVerifyingDoc] = useState<DocumentMetadata | null>(null);
    const [verifyNotes, setVerifyNotes] = useState<string>('');
    const [isVerifying, setIsVerifying] = useState<boolean>(false);

    const [rejectingDoc, setRejectingDoc] = useState<DocumentMetadata | null>(null);
    const [rejectionReason, setRejectionReason] = useState<string>('');
    const [isRejecting, setIsRejecting] = useState<boolean>(false);

    const [editingDoc, setEditingDoc] = useState<DocumentMetadata | null>(null);
    const [editType, setEditType] = useState<string>('');
    const [editIssueDate, setEditIssueDate] = useState<string>('');
    const [editExpiryDate, setEditExpiryDate] = useState<string>('');
    const [isEditing, setIsEditing] = useState<boolean>(false);

    const [deletingDoc, setDeletingDoc] = useState<DocumentMetadata | null>(null);
    const [isDeleting, setIsDeleting] = useState<boolean>(false);

    // Active engagement ID
    const activeEngagementId = isCustomEngagement ? customEngagementInput : selectedEngagementId;

    // Load engagements list
    useEffect(() => {
        const fetchEngagements = async () => {
            try {
                const data = await WorkflowApi.getEngagements(tenantId);
                if (data && data.length > 0) {
                    setEngagements(data);
                    if (!selectedEngagementId || selectedEngagementId === 'e689ce2c-b694-4860-aa0d-96d946283b71') {
                        setSelectedEngagementId(data[0].engagementId);
                    }
                }
            } catch (e) {
                console.warn('Could not load engagement list, defaulting to manual entry', e);
            }
        };
        fetchEngagements();
    }, [tenantId]);

    // Fetch documents
    const fetchDocuments = async () => {
        if (!activeEngagementId) return;
        setLoading(true);
        setError(null);
        try {
            const filter = {
                type: filterType !== 'ALL' ? filterType : undefined,
                complianceStatus: filterCompliance !== 'ALL' ? filterCompliance : undefined,
                verificationStatus: filterVerification !== 'ALL' ? filterVerification : undefined,
                includeDeleted: includeDeleted,
            };
            const data = await DocumentsApi.getDocuments(activeEngagementId, filter, tenantId);
            setDocuments(data || []);
        } catch (err: any) {
            setError(err.message || 'Failed to fetch documents from Document Vault Microservice.');
        } finally {
            setLoading(false);
        }
    };

    useEffect(() => {
        fetchDocuments();
    }, [activeEngagementId, tenantId, filterType, filterCompliance, filterVerification, includeDeleted]);

    // Show temporary success banner
    const showSuccess = (msg: string) => {
        setSuccessBanner(msg);
        setTimeout(() => setSuccessBanner(null), 4500);
    };

    // Client-side search filtering
    const filteredDocuments = useMemo(() => {
        if (!searchQuery.trim()) return documents;
        const q = searchQuery.toLowerCase();
        return documents.filter(
            (d) =>
                d.documentId.toLowerCase().includes(q) ||
                d.type.toLowerCase().includes(q) ||
                (d.uploaderId && d.uploaderId.toLowerCase().includes(q)) ||
                (d.verificationReason && d.verificationReason.toLowerCase().includes(q)) ||
                (d.rejectionReason && d.rejectionReason.toLowerCase().includes(q))
        );
    }, [documents, searchQuery]);

    // File selection validation
    const handleFileChange = (e: React.ChangeEvent<HTMLInputElement>) => {
        setUploadError(null);
        if (e.target.files && e.target.files[0]) {
            const file = e.target.files[0];
            if (!file.name.toLowerCase().endsWith('.pdf') && file.type !== 'application/pdf') {
                setUploadError('Only PDF files (.pdf) are allowed.');
                e.target.value = '';
                setUploadFile(null);
                return;
            }
            if (file.size > 5 * 1024 * 1024) {
                setUploadError('File size exceeds the 5MB maximum limit.');
                e.target.value = '';
                setUploadFile(null);
                return;
            }
            setUploadFile(file);
        }
    };

    // Upload Handler
    const handleUpload = async (e: React.FormEvent) => {
        e.preventDefault();
        setUploadError(null);

        if (!uploadFile) {
            setUploadError('Please select a PDF document to upload.');
            return;
        }

        if (!issueDate || !expiryDate) {
            setUploadError('Issue Date and Expiry Date are mandatory.');
            return;
        }

        if (new Date(expiryDate) <= new Date(issueDate)) {
            setUploadError('Expiry Date must be later than Issue Date.');
            return;
        }

        setUploading(true);
        const formData = new FormData();
        formData.append('file', uploadFile);
        formData.append('type', uploadType);
        formData.append('issueDate', issueDate);
        formData.append('expiryDate', expiryDate);
        formData.append('uploaderId', userId || 'user-staff-1');

        try {
            await DocumentsApi.uploadDocument(activeEngagementId, formData, tenantId);
            showSuccess(`Document uploaded successfully to Document Vault.`);
            setUploadFile(null);
            setIsUploadDrawerOpen(false);
            fetchDocuments();
        } catch (err: any) {
            setUploadError(err.message || 'Upload failed.');
        } finally {
            setUploading(false);
        }
    };

    // Sync workflow action on verification/rejection
    const syncWorkflowActionVerification = async (
        status: 'Verified' | 'Rejected',
        reason?: string
    ) => {
        try {
            const actions = await WorkflowApi.getActions(activeEngagementId, tenantId);
            const linkedAction = actions.find(
                (a: ClientAction) =>
                    (a.type === 'DocumentUpload' || a.type === 'KycDocument') && !a.isCompleted
            );
            if (linkedAction) {
                await WorkflowApi.applyVerification(
                    activeEngagementId,
                    linkedAction.actionId,
                    {
                        verificationStatus: status,
                        verificationReason: reason,
                        verifiedBy: userId || 'user-staff-1',
                    },
                    tenantId
                );
            }
        } catch (syncErr) {
            console.warn('Workflow microservice synchronization notification: ', syncErr);
        }
    };

    // Verify Document Handler
    const handleConfirmVerify = async () => {
        if (!verifyingDoc) return;
        setIsVerifying(true);
        try {
            await DocumentsApi.verifyDocument(
                activeEngagementId,
                verifyingDoc.documentId,
                {
                    staffActor: userId || 'user-staff-1',
                    staffNotes: verifyNotes.trim() || undefined,
                },
                tenantId
            );

            // Synchronize workflow state
            await syncWorkflowActionVerification('Verified', verifyNotes);

            showSuccess(`Document ${verifyingDoc.documentId.slice(0, 8)}... successfully verified!`);
            setVerifyingDoc(null);
            setVerifyNotes('');
            fetchDocuments();
        } catch (err: any) {
            alert('Verification failed: ' + (err.message || 'Server error'));
        } finally {
            setIsVerifying(false);
        }
    };

    // Reject Document Handler
    const handleConfirmReject = async () => {
        if (!rejectingDoc) return;
        if (!rejectionReason.trim()) {
            alert('Rejection reason is required.');
            return;
        }

        setIsRejecting(true);
        try {
            await DocumentsApi.rejectDocument(
                activeEngagementId,
                rejectingDoc.documentId,
                {
                    staffActor: userId || 'user-staff-1',
                    reason: rejectionReason.trim(),
                },
                tenantId
            );

            // Synchronize workflow state
            await syncWorkflowActionVerification('Rejected', rejectionReason);

            showSuccess(`Document ${rejectingDoc.documentId.slice(0, 8)}... marked as REJECTED.`);
            setRejectingDoc(null);
            setRejectionReason('');
            fetchDocuments();
        } catch (err: any) {
            alert('Rejection failed: ' + (err.message || 'Server error'));
        } finally {
            setIsRejecting(false);
        }
    };

    // Edit Metadata Handler
    const handleOpenEdit = (doc: DocumentMetadata) => {
        setEditingDoc(doc);
        setEditType(doc.type);
        setEditIssueDate(doc.issueDate || '');
        setEditExpiryDate(doc.expiryDate || '');
    };

    const handleConfirmEdit = async () => {
        if (!editingDoc) return;
        if (!editIssueDate || !editExpiryDate) {
            alert('Issue Date and Expiry Date are mandatory.');
            return;
        }
        if (new Date(editExpiryDate) <= new Date(editIssueDate)) {
            alert('Expiry Date must be after Issue Date.');
            return;
        }

        setIsEditing(true);
        try {
            await DocumentsApi.updateDocumentMetadata(
                activeEngagementId,
                editingDoc.documentId,
                {
                    type: editType,
                    issueDate: editIssueDate,
                    expiryDate: editExpiryDate,
                },
                tenantId
            );
            showSuccess(`Document metadata updated.`);
            setEditingDoc(null);
            fetchDocuments();
        } catch (err: any) {
            alert('Failed to update metadata: ' + (err.message || 'Server error'));
        } finally {
            setIsEditing(false);
        }
    };

    // Soft Delete Handler
    const handleConfirmDelete = async () => {
        if (!deletingDoc) return;
        setIsDeleting(true);
        try {
            await DocumentsApi.deleteDocument(activeEngagementId, deletingDoc.documentId, tenantId);
            showSuccess(`Document soft-deleted. Staff can view it by enabling "Show Deleted".`);
            setDeletingDoc(null);
            fetchDocuments();
        } catch (err: any) {
            alert('Delete failed: ' + (err.message || 'Server error'));
        } finally {
            setIsDeleting(false);
        }
    };

    return (
        <div className="space-y-6">
            {/* Header with Engagement Selector */}
            <div className="bg-white rounded-2xl border border-slate-200/80 p-6 shadow-sm">
                <div className="flex flex-col lg:flex-row lg:items-center justify-between gap-4">
                    <div>
                        <div className="flex items-center gap-2 mb-1">
                            <span className="inline-flex items-center gap-1.5 px-2.5 py-0.5 rounded-full text-xs font-semibold bg-indigo-50 text-indigo-700 border border-indigo-200/60">
                                <Lock className="w-3 h-3 text-indigo-600" />
                                Tier-1 Encrypted Storage
                            </span>
                            <span className="text-xs text-slate-400">•</span>
                            <span className="text-xs text-slate-500 font-medium">Deterministic Compliance</span>
                        </div>
                        <h1 className="text-2xl font-bold text-slate-900 tracking-tight flex items-center gap-2">
                            <FileCheck className="w-6 h-6 text-indigo-600" />
                            Document Vault & Verification Engine
                        </h1>
                        <p className="text-sm text-slate-500 mt-1 max-w-2xl">
                            Verify evidence documents, manage multi-criteria lifecycles, conduct soft-deletes, and retain continuous audit accessibility.
                        </p>
                    </div>

                    {/* Engagement Picker */}
                    <div className="flex flex-col sm:flex-row items-stretch sm:items-center gap-3 bg-slate-50/80 p-3 rounded-xl border border-slate-200">
                        <div className="flex items-center gap-2 text-xs font-bold text-slate-600 uppercase tracking-wider">
                            <Building className="w-4 h-4 text-indigo-500" />
                            Engagement:
                        </div>

                        {!isCustomEngagement ? (
                            <select
                                value={selectedEngagementId}
                                onChange={(e) => setSelectedEngagementId(e.target.value)}
                                className="text-xs font-medium bg-white border border-slate-200 rounded-lg px-3 py-2 text-slate-800 focus:outline-none focus:ring-2 focus:ring-indigo-500/20 focus:border-indigo-500 shadow-sm"
                            >
                                {engagements.map((eng) => (
                                    <option key={eng.engagementId} value={eng.engagementId}>
                                        {eng.clientId ? `Client ${eng.clientId.slice(0, 8)}...` : eng.engagementId.slice(0, 8)} ({eng.status})
                                    </option>
                                ))}
                                {engagements.length === 0 && (
                                    <option value={selectedEngagementId}>{selectedEngagementId.slice(0, 8)}...</option>
                                )}
                            </select>
                        ) : (
                            <input
                                type="text"
                                placeholder="Enter engagement UUID..."
                                value={customEngagementInput}
                                onChange={(e) => setCustomEngagementInput(e.target.value)}
                                className="text-xs font-mono bg-white border border-slate-200 rounded-lg px-3 py-2 text-slate-800 focus:outline-none focus:ring-2 focus:ring-indigo-500/20 focus:border-indigo-500 w-64 shadow-sm"
                            />
                        )}

                        <button
                            type="button"
                            onClick={() => {
                                setIsCustomEngagement(!isCustomEngagement);
                                if (!isCustomEngagement && !customEngagementInput) {
                                    setCustomEngagementInput(selectedEngagementId);
                                }
                            }}
                            className="text-[11px] font-semibold text-indigo-600 hover:text-indigo-800 hover:underline px-1 py-1"
                        >
                            {isCustomEngagement ? 'Pick from list' : 'Custom UUID'}
                        </button>

                        <button
                            type="button"
                            onClick={fetchDocuments}
                            title="Refresh Document List"
                            className="p-2 rounded-lg bg-white border border-slate-200 text-slate-600 hover:text-indigo-600 hover:bg-slate-100 transition shadow-sm"
                        >
                            <RefreshCw className={`w-3.5 h-3.5 ${loading ? 'animate-spin' : ''}`} />
                        </button>
                    </div>
                </div>

                {/* Banner messages */}
                {successBanner && (
                    <div className="mt-4 p-3 rounded-xl bg-emerald-50 border border-emerald-200 text-emerald-800 text-xs font-medium flex items-center justify-between">
                        <div className="flex items-center gap-2">
                            <CheckCircle2 className="w-4 h-4 text-emerald-600 flex-shrink-0" />
                            <span>{successBanner}</span>
                        </div>
                        <button onClick={() => setSuccessBanner(null)} className="text-emerald-600 hover:text-emerald-900">
                            <X className="w-4 h-4" />
                        </button>
                    </div>
                )}

                {error && (
                    <div className="mt-4 p-3 rounded-xl bg-rose-50 border border-rose-200 text-rose-800 text-xs font-medium flex items-center justify-between">
                        <div className="flex items-center gap-2">
                            <AlertTriangle className="w-4 h-4 text-rose-600 flex-shrink-0" />
                            <span>{error}</span>
                        </div>
                        <button onClick={() => setError(null)} className="text-rose-600 hover:text-rose-900">
                            <X className="w-4 h-4" />
                        </button>
                    </div>
                )}
            </div>

            {/* Filter Bar & Action Header */}
            <div className="bg-white rounded-2xl border border-slate-200/80 p-5 shadow-sm space-y-4">
                <div className="flex flex-col md:flex-row md:items-center justify-between gap-4">
                    {/* Search Input */}
                    <div className="relative flex-1 max-w-md">
                        <Search className="w-4 h-4 text-slate-400 absolute left-3 top-1/2 -translate-y-1/2" />
                        <input
                            type="text"
                            placeholder="Search by ID, type, uploader, reason..."
                            value={searchQuery}
                            onChange={(e) => setSearchQuery(e.target.value)}
                            className="w-full pl-9 pr-4 py-2 text-xs bg-slate-50 border border-slate-200 rounded-xl text-slate-800 focus:outline-none focus:ring-2 focus:ring-indigo-500/20 focus:border-indigo-500 transition"
                        />
                        {searchQuery && (
                            <button
                                onClick={() => setSearchQuery('')}
                                className="absolute right-3 top-1/2 -translate-y-1/2 text-slate-400 hover:text-slate-600"
                            >
                                <X className="w-3.5 h-3.5" />
                            </button>
                        )}
                    </div>

                    {/* Upload Drawer Toggle Button */}
                    <button
                        type="button"
                        onClick={() => setIsUploadDrawerOpen(!isUploadDrawerOpen)}
                        className="inline-flex items-center gap-2 px-4 py-2 rounded-xl text-xs font-semibold text-white bg-gradient-to-r from-[#635bff] to-[#712ae2] hover:opacity-95 shadow-sm transition"
                    >
                        <UploadCloud className="w-4 h-4" />
                        {isUploadDrawerOpen ? 'Close Upload Panel' : 'Upload Evidence Document'}
                    </button>
                </div>

                {/* Filters Row */}
                <div className="flex flex-wrap items-center gap-3 pt-3 border-t border-slate-100 text-xs">
                    <div className="flex items-center gap-1.5 font-bold text-slate-500 uppercase tracking-wider text-[11px]">
                        <Filter className="w-3.5 h-3.5 text-indigo-500" />
                        Filter:
                    </div>

                    {/* Type Filter */}
                    <select
                        value={filterType}
                        onChange={(e) => setFilterType(e.target.value)}
                        className="bg-slate-50 border border-slate-200 rounded-lg px-2.5 py-1.5 font-medium text-slate-700 hover:border-slate-300 focus:outline-none focus:border-indigo-500"
                    >
                        <option value="ALL">All Document Types</option>
                        <option value="KYC_PASSPORT">KYC Passport</option>
                        <option value="PROOF_OF_ADDRESS">Proof of Address</option>
                        <option value="TAX_DECLARATION">Tax Declaration</option>
                        <option value="SIGNED_AGREEMENT">Signed Agreement</option>
                        <option value="CORPORATE_REGISTRY">Corporate Registry</option>
                        <option value="BANK_STATEMENT">Bank Statement</option>
                    </select>

                    {/* Compliance Filter */}
                    <select
                        value={filterCompliance}
                        onChange={(e) => setFilterCompliance(e.target.value)}
                        className="bg-slate-50 border border-slate-200 rounded-lg px-2.5 py-1.5 font-medium text-slate-700 hover:border-slate-300 focus:outline-none focus:border-indigo-500"
                    >
                        <option value="ALL">All Compliance</option>
                        <option value="Compliant">Compliant Only</option>
                        <option value="NonCompliant">Non-Compliant Only</option>
                        <option value="Pending">Pending Compliance</option>
                    </select>

                    {/* Verification Status Filter */}
                    <select
                        value={filterVerification}
                        onChange={(e) => setFilterVerification(e.target.value)}
                        className="bg-slate-50 border border-slate-200 rounded-lg px-2.5 py-1.5 font-medium text-slate-700 hover:border-slate-300 focus:outline-none focus:border-indigo-500"
                    >
                        <option value="ALL">All Verification</option>
                        <option value="VERIFIED">Staff Verified</option>
                        <option value="REJECTED">Staff Rejected</option>
                        <option value="PENDING">Pending Verification</option>
                    </select>

                    {/* Soft Deleted Toggle */}
                    <label className="flex items-center gap-2 cursor-pointer bg-slate-50 border border-slate-200 hover:border-slate-300 rounded-lg px-3 py-1.5 transition ml-auto">
                        <input
                            type="checkbox"
                            checked={includeDeleted}
                            onChange={(e) => setIncludeDeleted(e.target.checked)}
                            className="rounded border-slate-300 text-indigo-600 focus:ring-indigo-500 h-3.5 w-3.5"
                        />
                        <span className="font-semibold text-slate-700 text-xs">
                            Show Soft-Deleted Documents
                        </span>
                    </label>

                    {(filterType !== 'ALL' || filterCompliance !== 'ALL' || filterVerification !== 'ALL' || includeDeleted || searchQuery) && (
                        <button
                            onClick={() => {
                                setFilterType('ALL');
                                setFilterCompliance('ALL');
                                setFilterVerification('ALL');
                                setIncludeDeleted(false);
                                setSearchQuery('');
                            }}
                            className="text-[11px] font-semibold text-rose-600 hover:text-rose-800 hover:underline"
                        >
                            Reset Filters
                        </button>
                    )}
                </div>
            </div>

            {/* Collapsible Upload Panel */}
            {isUploadDrawerOpen && (
                <div className="bg-white rounded-2xl border-2 border-indigo-200/80 p-6 shadow-sm transition animate-in fade-in duration-200">
                    <div className="flex items-center justify-between pb-4 mb-4 border-b border-slate-100">
                        <div>
                            <h3 className="text-base font-bold text-slate-900 flex items-center gap-2">
                                <UploadCloud className="w-5 h-5 text-indigo-600" />
                                Upload Evidence to Document Vault
                            </h3>
                            <p className="text-xs text-slate-500 mt-0.5">
                                Real-time deterministic validation will check PDF format, 5MB size limit, and active validity date ranges.
                            </p>
                        </div>
                        <button
                            type="button"
                            onClick={() => setIsUploadDrawerOpen(false)}
                            className="text-slate-400 hover:text-slate-600 p-1"
                        >
                            <X className="w-5 h-5" />
                        </button>
                    </div>

                    <form onSubmit={handleUpload} className="space-y-4">
                        <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
                            {/* File Picker */}
                            <div>
                                <label className="block text-xs font-bold text-slate-700 mb-1.5">
                                    Select PDF Document <span className="text-rose-500">*</span>
                                </label>
                                <input
                                    type="file"
                                    accept=".pdf,application/pdf"
                                    onChange={handleFileChange}
                                    required
                                    className="w-full text-xs text-slate-600 file:mr-3 file:py-2 file:px-3 file:rounded-lg file:border-0 file:text-xs file:font-semibold file:bg-indigo-50 file:text-indigo-700 hover:file:bg-indigo-100 border border-slate-200 rounded-xl p-1 bg-slate-50 cursor-pointer"
                                />
                                <span className="text-[11px] text-slate-400 mt-1 block">Strictly PDF only • Maximum 5 MB</span>
                            </div>

                            {/* Document Type */}
                            <div>
                                <label className="block text-xs font-bold text-slate-700 mb-1.5">
                                    Document Classification Type <span className="text-rose-500">*</span>
                                </label>
                                <select
                                    value={uploadType}
                                    onChange={(e) => setUploadType(e.target.value)}
                                    className="w-full text-xs bg-slate-50 border border-slate-200 rounded-xl px-3 py-2.5 text-slate-800 focus:outline-none focus:ring-2 focus:ring-indigo-500/20 focus:border-indigo-500"
                                >
                                    <option value="KYC_PASSPORT">KYC Passport / Primary Identification</option>
                                    <option value="PROOF_OF_ADDRESS">Proof of Address / Utility Statement</option>
                                    <option value="TAX_DECLARATION">Tax Declaration Form (FATCA / CRS)</option>
                                    <option value="SIGNED_AGREEMENT">Signed B2B Master Service Agreement</option>
                                    <option value="CORPORATE_REGISTRY">Certificate of Incorporation / Registry</option>
                                    <option value="BANK_STATEMENT">Certified Corporate Bank Statement</option>
                                </select>
                            </div>
                        </div>

                        {/* Dates Row */}
                        <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
                            <div>
                                <label className="block text-xs font-bold text-slate-700 mb-1.5">
                                    Issue Date <span className="text-rose-500">*</span>
                                </label>
                                <input
                                    type="date"
                                    value={issueDate}
                                    onChange={(e) => setIssueDate(e.target.value)}
                                    required
                                    className="w-full text-xs bg-slate-50 border border-slate-200 rounded-xl px-3 py-2 text-slate-800 focus:outline-none focus:ring-2 focus:ring-indigo-500/20 focus:border-indigo-500"
                                />
                            </div>

                            <div>
                                <label className="block text-xs font-bold text-slate-700 mb-1.5">
                                    Expiry Date <span className="text-rose-500">*</span>
                                </label>
                                <input
                                    type="date"
                                    value={expiryDate}
                                    onChange={(e) => setExpiryDate(e.target.value)}
                                    required
                                    className="w-full text-xs bg-slate-50 border border-slate-200 rounded-xl px-3 py-2 text-slate-800 focus:outline-none focus:ring-2 focus:ring-indigo-500/20 focus:border-indigo-500"
                                />
                            </div>
                        </div>

                        {uploadError && (
                            <div className="p-3 rounded-xl bg-rose-50 border border-rose-200 text-rose-700 text-xs font-medium flex items-center gap-2">
                                <AlertTriangle className="w-4 h-4 flex-shrink-0" />
                                {uploadError}
                            </div>
                        )}

                        <div className="flex items-center justify-end gap-3 pt-2">
                            <button
                                type="button"
                                onClick={() => setIsUploadDrawerOpen(false)}
                                className="px-4 py-2 text-xs font-semibold text-slate-600 hover:text-slate-800 transition"
                            >
                                Cancel
                            </button>
                            <button
                                type="submit"
                                disabled={uploading || !uploadFile}
                                className="inline-flex items-center gap-2 px-5 py-2.5 rounded-xl text-xs font-semibold text-white bg-gradient-to-r from-[#635bff] to-[#712ae2] hover:opacity-95 shadow-sm transition disabled:opacity-50"
                            >
                                {uploading ? (
                                    <>
                                        <RefreshCw className="w-3.5 h-3.5 animate-spin" />
                                        Deterministic Validation Running...
                                    </>
                                ) : (
                                    <>
                                        <UploadCloud className="w-4 h-4" />
                                        Upload & Ingest to Vault
                                    </>
                                )}
                            </button>
                        </div>
                    </form>
                </div>
            )}

            {/* Documents List Card */}
            <div className="bg-white rounded-2xl border border-slate-200/80 shadow-sm overflow-hidden">
                <div className="px-6 py-4 border-b border-slate-100 flex items-center justify-between">
                    <div className="flex items-center gap-2">
                        <FileText className="w-5 h-5 text-indigo-600" />
                        <h2 className="text-base font-bold text-slate-900">
                            Vault Documents Repository
                        </h2>
                        <span className="px-2 py-0.5 rounded-full text-xs font-semibold bg-slate-100 text-slate-700 border border-slate-200">
                            {filteredDocuments.length} {filteredDocuments.length === 1 ? 'file' : 'files'}
                        </span>
                    </div>

                    <div className="text-xs text-slate-400 font-mono">
                        Engagement: {activeEngagementId.slice(0, 13)}...
                    </div>
                </div>

                {loading ? (
                    <div className="py-16 text-center">
                        <RefreshCw className="w-8 h-8 text-indigo-500 animate-spin mx-auto mb-3" />
                        <p className="text-sm font-medium text-slate-600">Retrieving encrypted vault metadata...</p>
                    </div>
                ) : filteredDocuments.length === 0 ? (
                    <div className="py-16 text-center px-4">
                        <div className="w-14 h-14 rounded-2xl bg-slate-100 flex items-center justify-center mx-auto mb-3 text-slate-400">
                            <FileText className="w-7 h-7" />
                        </div>
                        <h3 className="text-sm font-bold text-slate-800 mb-1">No Documents Found</h3>
                        <p className="text-xs text-slate-500 max-w-sm mx-auto mb-4">
                            {documents.length === 0
                                ? 'No documents have been uploaded for this engagement yet.'
                                : 'No documents match the current active filter criteria.'}
                        </p>
                        <button
                            onClick={() => setIsUploadDrawerOpen(true)}
                            className="inline-flex items-center gap-1.5 px-3 py-1.5 rounded-lg text-xs font-semibold text-indigo-600 bg-indigo-50 hover:bg-indigo-100 transition"
                        >
                            <UploadCloud className="w-3.5 h-3.5" />
                            Upload First Document
                        </button>
                    </div>
                ) : (
                    <div className="overflow-x-auto">
                        <table className="w-full text-left border-collapse">
                            <thead>
                                <tr className="bg-slate-50/75 border-b border-slate-200/80 text-[11px] font-bold text-slate-500 uppercase tracking-wider">
                                    <th className="py-3 px-4">Document Details</th>
                                    <th className="py-3 px-4">Deterministic Compliance</th>
                                    <th className="py-3 px-4">Staff Verification</th>
                                    <th className="py-3 px-4">Validity Dates</th>
                                    <th className="py-3 px-4">Lifecycle</th>
                                    <th className="py-3 px-4 text-right">Actions</th>
                                </tr>
                            </thead>
                            <tbody className="divide-y divide-slate-100 text-xs">
                                {filteredDocuments.map((doc) => {
                                    const isDeleted = doc.isDeleted;
                                    const isCompliant = doc.complianceStatus === 'Compliant';
                                    const isVerified = doc.verificationStatus?.toUpperCase() === 'VERIFIED';
                                    const isRejected = doc.verificationStatus?.toUpperCase() === 'REJECTED';

                                    return (
                                        <tr
                                            key={doc.documentId}
                                            className={`hover:bg-slate-50/60 transition ${
                                                isDeleted ? 'bg-slate-50/40 opacity-75' : ''
                                            }`}
                                        >
                                            {/* Document Details */}
                                            <td className="py-3.5 px-4">
                                                <div className="flex items-start gap-3">
                                                    <div className={`p-2 rounded-xl border mt-0.5 ${
                                                        isDeleted
                                                            ? 'bg-slate-100 border-slate-200 text-slate-400'
                                                            : 'bg-indigo-50/70 border-indigo-100 text-indigo-600'
                                                    }`}>
                                                        <FileText className="w-4 h-4" />
                                                    </div>
                                                    <div>
                                                        <div className="font-semibold text-slate-900 flex items-center gap-1.5">
                                                            <span className={isDeleted ? 'line-through text-slate-500' : ''}>
                                                                {doc.type}
                                                            </span>
                                                        </div>
                                                        <div className="text-[11px] font-mono text-slate-400 mt-0.5" title={doc.documentId}>
                                                            ID: {doc.documentId.slice(0, 14)}...
                                                        </div>
                                                        <div className="text-[11px] text-slate-400 mt-0.5">
                                                            Uploaded: {new Date(doc.uploadedAt).toLocaleDateString()} by {doc.uploaderId || 'Client'}
                                                        </div>
                                                    </div>
                                                </div>
                                            </td>

                                            {/* Deterministic Compliance */}
                                            <td className="py-3.5 px-4">
                                                {doc.complianceStatus === 'Compliant' ? (
                                                    <span className="inline-flex items-center gap-1.5 px-2.5 py-1 rounded-full text-xs font-semibold bg-emerald-50 text-emerald-700 border border-emerald-200/80">
                                                        <ShieldCheck className="w-3.5 h-3.5 text-emerald-600" />
                                                        Compliant
                                                    </span>
                                                ) : doc.complianceStatus === 'NonCompliant' ? (
                                                    <div>
                                                        <span className="inline-flex items-center gap-1.5 px-2.5 py-1 rounded-full text-xs font-semibold bg-rose-50 text-rose-700 border border-rose-200/80">
                                                            <ShieldAlert className="w-3.5 h-3.5 text-rose-600" />
                                                            Non-Compliant
                                                        </span>
                                                        {doc.rejectionReason && (
                                                            <div className="text-[11px] text-rose-600 font-medium mt-1 max-w-xs break-words">
                                                                {doc.rejectionReason}
                                                            </div>
                                                        )}
                                                    </div>
                                                ) : (
                                                    <span className="inline-flex items-center gap-1.5 px-2.5 py-1 rounded-full text-xs font-semibold bg-amber-50 text-amber-700 border border-amber-200/80">
                                                        <Clock className="w-3.5 h-3.5 text-amber-600" />
                                                        Pending Check
                                                    </span>
                                                )}
                                            </td>

                                            {/* Staff Verification */}
                                            <td className="py-3.5 px-4">
                                                {isVerified ? (
                                                    <div>
                                                        <span className="inline-flex items-center gap-1.5 px-2.5 py-1 rounded-full text-xs font-semibold bg-emerald-100/90 text-emerald-800 border border-emerald-300">
                                                            <CheckCircle2 className="w-3.5 h-3.5 text-emerald-700" />
                                                            Verified
                                                        </span>
                                                        {doc.verifiedBy && (
                                                            <div className="text-[10px] text-slate-500 font-medium mt-0.5">
                                                                By: {doc.verifiedBy}
                                                            </div>
                                                        )}
                                                        {doc.verificationReason && (
                                                            <div className="text-[11px] text-slate-600 italic mt-0.5 max-w-xs">
                                                                "{doc.verificationReason}"
                                                            </div>
                                                        )}
                                                    </div>
                                                ) : isRejected ? (
                                                    <div>
                                                        <span className="inline-flex items-center gap-1.5 px-2.5 py-1 rounded-full text-xs font-semibold bg-rose-100/90 text-rose-800 border border-rose-300">
                                                            <XCircle className="w-3.5 h-3.5 text-rose-700" />
                                                            Rejected
                                                        </span>
                                                        {doc.verificationReason && (
                                                            <div className="text-[11px] text-rose-600 font-medium mt-0.5 max-w-xs">
                                                                Reason: {doc.verificationReason}
                                                            </div>
                                                        )}
                                                    </div>
                                                ) : (
                                                    <span className="inline-flex items-center gap-1.5 px-2.5 py-1 rounded-full text-xs font-semibold bg-slate-100 text-slate-600 border border-slate-200">
                                                        <Clock className="w-3.5 h-3.5 text-slate-400" />
                                                        Pending Review
                                                    </span>
                                                )}
                                            </td>

                                            {/* Validity Dates */}
                                            <td className="py-3.5 px-4">
                                                <div className="text-[11px] text-slate-600 space-y-0.5">
                                                    <div className="flex items-center gap-1">
                                                        <span className="text-slate-400 font-medium">Issued:</span>
                                                        <span className="font-semibold text-slate-700">{doc.issueDate || '—'}</span>
                                                    </div>
                                                    <div className="flex items-center gap-1">
                                                        <span className="text-slate-400 font-medium">Expires:</span>
                                                        <span className={`font-semibold ${
                                                            doc.expiryDate && new Date(doc.expiryDate) < new Date()
                                                                ? 'text-rose-600 font-bold'
                                                                : 'text-slate-700'
                                                        }`}>
                                                            {doc.expiryDate || '—'}
                                                        </span>
                                                    </div>
                                                </div>
                                            </td>

                                            {/* Lifecycle */}
                                            <td className="py-3.5 px-4">
                                                {isDeleted ? (
                                                    <span className="inline-flex items-center gap-1 px-2 py-0.5 rounded text-[11px] font-semibold bg-slate-100 text-slate-500 border border-slate-300">
                                                        Soft-Deleted
                                                    </span>
                                                ) : (
                                                    <span className="inline-flex items-center gap-1 px-2 py-0.5 rounded text-[11px] font-semibold bg-sky-50 text-sky-700 border border-sky-200">
                                                        Active
                                                    </span>
                                                )}
                                            </td>

                                            {/* Actions */}
                                            <td className="py-3.5 px-4 text-right">
                                                <div className="flex items-center justify-end gap-1.5">
                                                    {/* Download Button - Enabled even if soft-deleted per requirement 4 */}
                                                    <a
                                                        href={DocumentsApi.getDownloadUrl(
                                                            activeEngagementId,
                                                            doc.documentId,
                                                            tenantId,
                                                            doc.isDeleted
                                                        )}
                                                        target="_blank"
                                                        rel="noreferrer"
                                                        title="Download PDF Evidence"
                                                        className="p-1.5 rounded-lg border border-slate-200 text-slate-600 hover:text-indigo-600 hover:border-indigo-200 hover:bg-indigo-50/50 transition"
                                                    >
                                                        <Download className="w-3.5 h-3.5" />
                                                    </a>

                                                    {/* Verify & Reject Buttons - Staff Verification */}
                                                    {!isDeleted && !isVerified && (
                                                        <>
                                                            <button
                                                                type="button"
                                                                disabled={!isCompliant}
                                                                onClick={() => {
                                                                    setVerifyingDoc(doc);
                                                                    setVerifyNotes('');
                                                                }}
                                                                title={
                                                                    isCompliant
                                                                        ? 'Verify Document Evidence'
                                                                        : 'Document must be Compliant before staff verification'
                                                                }
                                                                className="px-2 py-1 rounded-lg text-xs font-semibold text-emerald-700 bg-emerald-50 hover:bg-emerald-100 border border-emerald-200 transition disabled:opacity-40 disabled:cursor-not-allowed flex items-center gap-1"
                                                            >
                                                                <Check className="w-3.5 h-3.5" />
                                                                Verify
                                                            </button>

                                                            <button
                                                                type="button"
                                                                onClick={() => {
                                                                    setRejectingDoc(doc);
                                                                    setRejectionReason('');
                                                                }}
                                                                title="Reject Document Evidence"
                                                                className="px-2 py-1 rounded-lg text-xs font-semibold text-rose-700 bg-rose-50 hover:bg-rose-100 border border-rose-200 transition flex items-center gap-1"
                                                            >
                                                                <X className="w-3.5 h-3.5" />
                                                                Reject
                                                            </button>
                                                        </>
                                                    )}

                                                    {/* Edit Metadata */}
                                                    {!isDeleted && (
                                                        <button
                                                            type="button"
                                                            onClick={() => handleOpenEdit(doc)}
                                                            title="Edit Document Metadata"
                                                            className="p-1.5 rounded-lg border border-slate-200 text-slate-600 hover:text-indigo-600 hover:border-indigo-200 hover:bg-indigo-50/50 transition"
                                                        >
                                                            <Edit3 className="w-3.5 h-3.5" />
                                                        </button>
                                                    )}

                                                    {/* Soft Delete */}
                                                    {!isDeleted && (
                                                        <button
                                                            type="button"
                                                            onClick={() => setDeletingDoc(doc)}
                                                            title="Soft Delete Document"
                                                            className="p-1.5 rounded-lg border border-slate-200 text-slate-500 hover:text-rose-600 hover:border-rose-200 hover:bg-rose-50/50 transition"
                                                        >
                                                            <Trash2 className="w-3.5 h-3.5" />
                                                        </button>
                                                    )}
                                                </div>
                                            </td>
                                        </tr>
                                    );
                                })}
                            </tbody>
                        </table>
                    </div>
                )}
            </div>

            {/* =========================================================================
                MODALS
               ========================================================================= */}

            {/* 1. Verification Modal */}
            {verifyingDoc && (
                <div className="fixed inset-0 z-50 bg-slate-900/50 backdrop-blur-sm flex items-center justify-center p-4">
                    <div className="bg-white rounded-2xl max-w-md w-full p-6 shadow-2xl border border-slate-200 space-y-4 animate-in fade-in zoom-in-95 duration-150">
                        <div className="flex items-center gap-3">
                            <div className="p-2.5 rounded-xl bg-emerald-50 text-emerald-600 border border-emerald-200">
                                <CheckCircle2 className="w-6 h-6" />
                            </div>
                            <div>
                                <h3 className="text-base font-bold text-slate-900">
                                    Verify Document Evidence
                                </h3>
                                <p className="text-xs text-slate-500">
                                    Approve this document for engagement compliance audit.
                                </p>
                            </div>
                        </div>

                        <div className="bg-slate-50 p-3 rounded-xl border border-slate-200 text-xs space-y-1">
                            <div><span className="font-semibold text-slate-700">Type:</span> {verifyingDoc.type}</div>
                            <div><span className="font-semibold text-slate-700">Doc ID:</span> <span className="font-mono">{verifyingDoc.documentId}</span></div>
                            <div><span className="font-semibold text-slate-700">Staff Verifier:</span> {userId || 'user-staff-1'}</div>
                        </div>

                        <div>
                            <label className="block text-xs font-bold text-slate-700 mb-1">
                                Verification Notes (Optional)
                            </label>
                            <textarea
                                rows={3}
                                value={verifyNotes}
                                onChange={(e) => setVerifyNotes(e.target.value)}
                                placeholder="E.g., Passport photo and MRZ inspected, corporate seals verified..."
                                className="w-full text-xs bg-slate-50 border border-slate-200 rounded-xl p-3 text-slate-800 focus:outline-none focus:ring-2 focus:ring-emerald-500/20 focus:border-emerald-500"
                            />
                        </div>

                        <div className="flex items-center justify-end gap-3 pt-2">
                            <button
                                type="button"
                                onClick={() => setVerifyingDoc(null)}
                                className="px-4 py-2 text-xs font-semibold text-slate-600 hover:text-slate-800 transition"
                            >
                                Cancel
                            </button>
                            <button
                                type="button"
                                disabled={isVerifying}
                                onClick={handleConfirmVerify}
                                className="inline-flex items-center gap-2 px-5 py-2.5 rounded-xl text-xs font-semibold text-white bg-emerald-600 hover:bg-emerald-700 shadow-sm transition disabled:opacity-50"
                            >
                                {isVerifying ? (
                                    <>
                                        <RefreshCw className="w-3.5 h-3.5 animate-spin" />
                                        Verifying...
                                    </>
                                ) : (
                                    <>
                                        <Check className="w-4 h-4" />
                                        Confirm Verification
                                    </>
                                )}
                            </button>
                        </div>
                    </div>
                </div>
            )}

            {/* 2. Rejection Modal */}
            {rejectingDoc && (
                <div className="fixed inset-0 z-50 bg-slate-900/50 backdrop-blur-sm flex items-center justify-center p-4">
                    <div className="bg-white rounded-2xl max-w-md w-full p-6 shadow-2xl border border-slate-200 space-y-4 animate-in fade-in zoom-in-95 duration-150">
                        <div className="flex items-center gap-3">
                            <div className="p-2.5 rounded-xl bg-rose-50 text-rose-600 border border-rose-200">
                                <XCircle className="w-6 h-6" />
                            </div>
                            <div>
                                <h3 className="text-base font-bold text-slate-900">
                                    Reject Document Evidence
                                </h3>
                                <p className="text-xs text-slate-500">
                                    Mark this document as rejected. Provide clear reason for client re-submission.
                                </p>
                            </div>
                        </div>

                        <div className="bg-slate-50 p-3 rounded-xl border border-slate-200 text-xs space-y-1">
                            <div><span className="font-semibold text-slate-700">Type:</span> {rejectingDoc.type}</div>
                            <div><span className="font-semibold text-slate-700">Doc ID:</span> <span className="font-mono">{rejectingDoc.documentId}</span></div>
                        </div>

                        <div>
                            <label className="block text-xs font-bold text-slate-700 mb-1">
                                Rejection Reason <span className="text-rose-500">*</span>
                            </label>
                            <textarea
                                rows={3}
                                required
                                value={rejectionReason}
                                onChange={(e) => setRejectionReason(e.target.value)}
                                placeholder="E.g., Document scan is illegible, signature does not match registry..."
                                className="w-full text-xs bg-slate-50 border border-slate-200 rounded-xl p-3 text-slate-800 focus:outline-none focus:ring-2 focus:ring-rose-500/20 focus:border-rose-500"
                            />
                        </div>

                        <div className="flex items-center justify-end gap-3 pt-2">
                            <button
                                type="button"
                                onClick={() => setRejectingDoc(null)}
                                className="px-4 py-2 text-xs font-semibold text-slate-600 hover:text-slate-800 transition"
                            >
                                Cancel
                            </button>
                            <button
                                type="button"
                                disabled={isRejecting || !rejectionReason.trim()}
                                onClick={handleConfirmReject}
                                className="inline-flex items-center gap-2 px-5 py-2.5 rounded-xl text-xs font-semibold text-white bg-rose-600 hover:bg-rose-700 shadow-sm transition disabled:opacity-50"
                            >
                                {isRejecting ? (
                                    <>
                                        <RefreshCw className="w-3.5 h-3.5 animate-spin" />
                                        Rejecting...
                                    </>
                                ) : (
                                    <>
                                        <X className="w-4 h-4" />
                                        Confirm Rejection
                                    </>
                                )}
                            </button>
                        </div>
                    </div>
                </div>
            )}

            {/* 3. Edit Metadata Modal */}
            {editingDoc && (
                <div className="fixed inset-0 z-50 bg-slate-900/50 backdrop-blur-sm flex items-center justify-center p-4">
                    <div className="bg-white rounded-2xl max-w-md w-full p-6 shadow-2xl border border-slate-200 space-y-4 animate-in fade-in zoom-in-95 duration-150">
                        <div className="flex items-center gap-3">
                            <div className="p-2.5 rounded-xl bg-indigo-50 text-indigo-600 border border-indigo-200">
                                <Edit3 className="w-6 h-6" />
                            </div>
                            <div>
                                <h3 className="text-base font-bold text-slate-900">
                                    Edit Document Metadata
                                </h3>
                                <p className="text-xs text-slate-500">
                                    Update classification type, issue date, and expiry date.
                                </p>
                            </div>
                        </div>

                        <div className="space-y-3">
                            <div>
                                <label className="block text-xs font-bold text-slate-700 mb-1">
                                    Document Type
                                </label>
                                <select
                                    value={editType}
                                    onChange={(e) => setEditType(e.target.value)}
                                    className="w-full text-xs bg-slate-50 border border-slate-200 rounded-xl px-3 py-2.5 text-slate-800 focus:outline-none focus:ring-2 focus:ring-indigo-500/20 focus:border-indigo-500"
                                >
                                    <option value="KYC_PASSPORT">KYC Passport / Identification</option>
                                    <option value="PROOF_OF_ADDRESS">Proof of Address / Utility Bill</option>
                                    <option value="TAX_DECLARATION">Tax Declaration Form</option>
                                    <option value="SIGNED_AGREEMENT">Signed B2B Master Agreement</option>
                                    <option value="CORPORATE_REGISTRY">Corporate Registry</option>
                                    <option value="BANK_STATEMENT">Bank Statement</option>
                                </select>
                            </div>

                            <div>
                                <label className="block text-xs font-bold text-slate-700 mb-1">
                                    Issue Date <span className="text-rose-500">*</span>
                                </label>
                                <input
                                    type="date"
                                    value={editIssueDate}
                                    onChange={(e) => setEditIssueDate(e.target.value)}
                                    required
                                    className="w-full text-xs bg-slate-50 border border-slate-200 rounded-xl px-3 py-2 text-slate-800 focus:outline-none focus:ring-2 focus:ring-indigo-500/20 focus:border-indigo-500"
                                />
                            </div>

                            <div>
                                <label className="block text-xs font-bold text-slate-700 mb-1">
                                    Expiry Date <span className="text-rose-500">*</span>
                                </label>
                                <input
                                    type="date"
                                    value={editExpiryDate}
                                    onChange={(e) => setEditExpiryDate(e.target.value)}
                                    required
                                    className="w-full text-xs bg-slate-50 border border-slate-200 rounded-xl px-3 py-2 text-slate-800 focus:outline-none focus:ring-2 focus:ring-indigo-500/20 focus:border-indigo-500"
                                />
                            </div>
                        </div>

                        <div className="flex items-center justify-end gap-3 pt-2">
                            <button
                                type="button"
                                onClick={() => setEditingDoc(null)}
                                className="px-4 py-2 text-xs font-semibold text-slate-600 hover:text-slate-800 transition"
                            >
                                Cancel
                            </button>
                            <button
                                type="button"
                                disabled={isEditing}
                                onClick={handleConfirmEdit}
                                className="inline-flex items-center gap-2 px-5 py-2.5 rounded-xl text-xs font-semibold text-white bg-indigo-600 hover:bg-indigo-700 shadow-sm transition disabled:opacity-50"
                            >
                                {isEditing ? (
                                    <>
                                        <RefreshCw className="w-3.5 h-3.5 animate-spin" />
                                        Saving...
                                    </>
                                ) : (
                                    'Save Metadata Changes'
                                )}
                            </button>
                        </div>
                    </div>
                </div>
            )}

            {/* 4. Soft Delete Confirmation Modal */}
            {deletingDoc && (
                <div className="fixed inset-0 z-50 bg-slate-900/50 backdrop-blur-sm flex items-center justify-center p-4">
                    <div className="bg-white rounded-2xl max-w-md w-full p-6 shadow-2xl border border-slate-200 space-y-4 animate-in fade-in zoom-in-95 duration-150">
                        <div className="flex items-center gap-3">
                            <div className="p-2.5 rounded-xl bg-rose-50 text-rose-600 border border-rose-200">
                                <Trash2 className="w-6 h-6" />
                            </div>
                            <div>
                                <h3 className="text-base font-bold text-slate-900">
                                    Soft Delete Document?
                                </h3>
                                <p className="text-xs text-slate-500">
                                    The document will be removed from active client views.
                                </p>
                            </div>
                        </div>

                        <div className="p-3 bg-amber-50 border border-amber-200 rounded-xl text-xs text-amber-800 space-y-1">
                            <div className="font-bold flex items-center gap-1.5">
                                <Info className="w-4 h-4 text-amber-600 flex-shrink-0" />
                                Continuous Staff Access Guarantee:
                            </div>
                            <div>
                                Staff can still view and download this document at any time by toggling <strong>"Show Soft-Deleted Documents"</strong>.
                            </div>
                        </div>

                        <div className="flex items-center justify-end gap-3 pt-2">
                            <button
                                type="button"
                                onClick={() => setDeletingDoc(null)}
                                className="px-4 py-2 text-xs font-semibold text-slate-600 hover:text-slate-800 transition"
                            >
                                Keep Document
                            </button>
                            <button
                                type="button"
                                disabled={isDeleting}
                                onClick={handleConfirmDelete}
                                className="inline-flex items-center gap-2 px-5 py-2.5 rounded-xl text-xs font-semibold text-white bg-rose-600 hover:bg-rose-700 shadow-sm transition disabled:opacity-50"
                            >
                                {isDeleting ? (
                                    <>
                                        <RefreshCw className="w-3.5 h-3.5 animate-spin" />
                                        Deleting...
                                    </>
                                ) : (
                                    <>
                                        <Trash2 className="w-4 h-4" />
                                        Soft Delete
                                    </>
                                )}
                            </button>
                        </div>
                    </div>
                </div>
            )}
        </div>
    );
};
