import React from 'react';
import { useAuth } from '../context/AuthContext';
import { UserRole } from '../types';

interface NavbarProps {
    activeTab: string;
    setActiveTab: (tab: string) => void;
}

export const Navbar: React.FC<NavbarProps> = ({ activeTab, setActiveTab }) => {
    const { tenantId, setTenantId, role, setRole } = useAuth();

    return (
        <header className="navbar">
            <div className="navbar-brand">
                <div className="logo-icon">🛡️</div>
                <div>
                    <h1 className="brand-title">Custodian</h1>
                    <p className="brand-subtitle">B2B Client Lifecycle Platform</p>
                </div>
            </div>

            <nav className="nav-links">
                <button
                    className={`nav-btn ${activeTab === 'engagements' ? 'active' : ''}`}
                    onClick={() => setActiveTab('engagements')}
                >
                    🔄 Engagements
                </button>
                <button
                    className={`nav-btn ${activeTab === 'actions' ? 'active' : ''}`}
                    onClick={() => setActiveTab('actions')}
                >
                    {role === 'Client' ? '📋 Client Portal' : '📋 Staff Actions'}
                </button>
                <button
                    className={`nav-btn ${activeTab === 'documents' ? 'active' : ''}`}
                    onClick={() => setActiveTab('documents')}
                >
                    📄 Document Vault
                </button>
                <button
                    className={`nav-btn ${activeTab === 'audit' ? 'active' : ''}`}
                    onClick={() => setActiveTab('audit')}
                >
                    📜 Genesis Audit Log
                </button>
            </nav>

            <div className="navbar-controls">
                <div className="control-group">
                    <label className="control-label">Tenant:</label>
                    <select
                        value={tenantId}
                        onChange={(e) => setTenantId(e.target.value)}
                        className="select-input"
                    >
                        <option value="tenant-alpha">Tenant Alpha</option>
                        <option value="tenant-beta">Tenant Beta</option>
                        <option value="qa-environment">QA Environment</option>
                    </select>
                </div>

                <div className="control-group">
                    <label className="control-label">Persona:</label>
                    <select
                        value={role}
                        onChange={(e) => setRole(e.target.value as UserRole)}
                        className="select-input role-select"
                    >
                        <option value="Staff">👨‍💼 Staff</option>
                        <option value="Client">👤 Client</option>
                        <option value="Admin">🛡️ Admin</option>
                    </select>
                </div>
            </div>
        </header>
    );
};
