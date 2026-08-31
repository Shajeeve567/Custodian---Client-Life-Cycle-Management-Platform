import React, { useState, useEffect } from 'react';
import { useAuth } from '../context/AuthContext';
import { DocumentMetadata } from '../types';
import { DocumentsApi } from '../services/api';

export const DocumentVaultView: React.FC = () => {
    const { tenantId, userId } = useAuth();
    const [engagementId, setEngagementId] = useState<string>('eng-1001');
    const [documents, setDocuments] = useState<DocumentMetadata[]>([]);
    const [loading, setLoading] = useState<boolean>(true);
    const [error, setError] = useState<string | null>(null);

    // Upload Form State
    const [file, setFile] = useState<File | null>(null);
    const [docType, setDocType] = useState<string>('KYC_PASSPORT');
    const [issueDate, setIssueDate] = useState<string>('2024-01-01');
    const [expiryDate, setExpiryDate] = useState<string>('2030-12-31');
    const [uploading, setUploading] = useState<boolean>(false);
    const [uploadMessage, setUploadMessage] = useState<string | null>(null);

    const fetchDocuments = async () => {
        if (!engagementId) return;
        setLoading(true);
        setError(null);
        try {
            const data = await DocumentsApi.getDocuments(engagementId, tenantId);
            setDocuments(data);
        } catch (err: any) {
            setError(err.message || 'Failed to fetch documents from Documents Microservice');
        } finally {
            setLoading(false);
        }
    };

    useEffect(() => {
        fetchDocuments();
    }, [engagementId, tenantId]);

    const handleFileChange = (e: React.ChangeEvent<HTMLInputElement>) => {
        if (e.target.files && e.target.files[0]) {
            const selected = e.target.files[0];

            // Strict Story 3 Acceptance Criteria: PDF only & max 5MB
            if (!selected.name.toLowerCase().endsWith('.pdf') && selected.type !== 'application/pdf') {
                alert('Validation Error: Only PDF documents (.pdf) are allowed.');
                e.target.value = '';
                setFile(null);
                return;
            }
            if (selected.size > 5 * 1024 * 1024) {
                alert('Validation Error: File size exceeds the 5MB maximum limit.');
                e.target.value = '';
                setFile(null);
                return;
            }

            setFile(selected);
        }
    };

    const handleUpload = async (e: React.FormEvent) => {
        e.preventDefault();
        if (!file) {
            alert('Please select a PDF file to upload.');
            return;
        }

        setUploading(true);
        setUploadMessage(null);

        const formData = new FormData();
        formData.append('file', file);
        formData.append('engagementId', engagementId);
        formData.append('type', docType);
        formData.append('issueDate', issueDate);
        formData.append('expiryDate', expiryDate);
        formData.append('uploaderId', userId);

        try {
            await DocumentsApi.uploadDocument(formData, tenantId);
            setUploadMessage('✅ Document uploaded successfully!');
            setFile(null);
            fetchDocuments();
        } catch (err: any) {
            alert('Upload failed: ' + err.message);
        } finally {
            setUploading(false);
        }
    };

    return (
        <div className="section-container">
            <div className="section-header">
                <div>
                    <h2>Document Vault & File Storage</h2>
                    <p className="section-desc">Upload, store, and verify PDF evidence documents linked to client engagements.</p>
                </div>
                <div className="engagement-picker">
                    <label>Engagement ID:</label>
                    <input
                        type="text"
                        value={engagementId}
                        onChange={(e) => setEngagementId(e.target.value)}
                        className="form-input font-mono"
                    />
                </div>
            </div>

            <div className="vault-layout">
                {/* Upload Form Card */}
                <div className="card upload-card">
                    <h3>📤 Upload PDF Document</h3>
                    <p className="text-muted text-sm mb-4">Mandatory requirements: PDF format only, max 5 MB size limit.</p>

                    <form onSubmit={handleUpload}>
                        <div className="form-group">
                            <label>Select PDF File:</label>
                            <input
                                type="file"
                                accept=".pdf,application/pdf"
                                onChange={handleFileChange}
                                required
                                className="form-input file-input"
                            />
                        </div>

                        <div className="form-group">
                            <label>Document Type:</label>
                            <select
                                value={docType}
                                onChange={(e) => setDocType(e.target.value)}
                                className="select-input"
                            >
                                <option value="KYC_PASSPORT">KYC Passport / Identification</option>
                                <option value="PROOF_OF_ADDRESS">Proof of Address / Utility Bill</option>
                                <option value="TAX_DECLARATION">Tax Declaration Form</option>
                                <option value="SIGNED_AGREEMENT">Signed B2B Master Agreement</option>
                            </select>
                        </div>

                        <div className="form-row">
                            <div className="form-group">
                                <label>Issue Date:</label>
                                <input
                                    type="date"
                                    value={issueDate}
                                    onChange={(e) => setIssueDate(e.target.value)}
                                    required
                                    className="form-input"
                                />
                            </div>
                            <div className="form-group">
                                <label>Expiry Date:</label>
                                <input
                                    type="date"
                                    value={expiryDate}
                                    onChange={(e) => setExpiryDate(e.target.value)}
                                    required
                                    className="form-input"
                                />
                            </div>
                        </div>

                        {uploadMessage && <div className="alert alert-success mt-3">{uploadMessage}</div>}

                        <button
                            type="submit"
                            className="btn btn-primary btn-block mt-4"
                            disabled={uploading || !file}
                        >
                            {uploading ? 'Uploading PDF...' : '📤 Upload to Documents Vault'}
                        </button>
                    </form>
                </div>

                {/* Documents Table */}
                <div className="card list-card">
                    <h3>📂 Engagement Documents ({documents.length})</h3>

                    {loading ? (
                        <div className="loading-skeleton">Loading document metadata...</div>
                    ) : documents.length === 0 ? (
                        <div className="empty-card">
                            <div className="empty-icon">📄</div>
                            <p>No PDF documents uploaded for engagement <strong>{engagementId}</strong>.</p>
                        </div>
                    ) : (
                        <div className="table-wrapper">
                            <table className="data-table">
                                <thead>
                                    <tr>
                                        <th>Doc ID</th>
                                        <th>Type</th>
                                        <th>Issue / Expiry</th>
                                        <th>Uploaded</th>
                                        <th>Action</th>
                                    </tr>
                                </thead>
                                <tbody>
                                    {documents.map((doc) => (
                                        <tr key={doc.documentId}>
                                            <td className="font-mono text-sm">{doc.documentId}</td>
                                            <td><span className="badge badge-type">{doc.type}</span></td>
                                            <td className="text-xs">
                                                <div>Iss: {doc.issueDate}</div>
                                                <div>Exp: {doc.expiryDate}</div>
                                            </td>
                                            <td className="text-xs text-muted">{new Date(doc.uploadedAt).toLocaleDateString()}</td>
                                            <td>
                                                <a
                                                    href={DocumentsApi.getDownloadUrl(doc.documentId)}
                                                    target="_blank"
                                                    rel="noreferrer"
                                                    className="btn btn-sm btn-outline-primary"
                                                >
                                                    ⬇️ Download PDF
                                                </a>
                                            </td>
                                        </tr>
                                    ))}
                                </tbody>
                            </table>
                        </div>
                    )}
                </div>
            </div>
        </div>
    );
};
