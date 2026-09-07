import React from 'react';
import { useAuth } from '../context/AuthContext';

interface NavbarProps {
    activeTab: string;
    setActiveTab: (tab: string) => void;
}

export const Navbar: React.FC<NavbarProps> = ({ activeTab, setActiveTab }) => {
    const { tenantId, tenantName, role, email, logout } = useAuth();

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
                    <span className="control-label">Tenant:</span>
                    <span className="font-semibold text-xs text-slate-800 bg-white px-2 py-1 rounded border">
                        {tenantName || tenantId || 'None'}
                    </span>
                </div>

                <div className="control-group">
                    <span className="control-label">Role:</span>
                    <span className="font-semibold text-xs text-indigo-700 bg-indigo-50 px-2 py-1 rounded border border-indigo-200">
                        {role}
                    </span>
                </div>

                <button onClick={logout} className="logout-button ml-2">
                    Sign Out
                </button>
            </div>
        </header>
    );
};
