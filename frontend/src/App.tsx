import React, { useState } from 'react';
import { AuthProvider, useAuth } from './context/AuthContext';
import { Navbar } from './components/Navbar';
import { EngagementManager } from './components/EngagementManager';
import { StaffActionHistoryView } from './components/StaffActionHistoryView';
import { ClientPortalView } from './components/ClientPortalView';
import { DocumentVaultView } from './components/DocumentVaultView';
import { AuditLogView } from './components/AuditLogView';
import { LoginView } from './components/LoginView';
import './App.css';

const MainAppContent: React.FC = () => {
    const [activeTab, setActiveTab] = useState<string>('engagements');
    const [engagementId] = useState<string>('e689ce2c-b694-4860-aa0d-96d946283b71');
    const { tenantId, role } = useAuth();

    return (
        <div className="app-layout">
            <Navbar activeTab={activeTab} setActiveTab={setActiveTab} />

            <main className="main-content">
                {activeTab === 'engagements' && <EngagementManager />}
                {activeTab === 'actions' && (
                    role === 'Client'
                        ? <ClientPortalView />
                        : <StaffActionHistoryView engagementId={engagementId} tenantId={tenantId} />
                )}
                {activeTab === 'documents' && <DocumentVaultView />}
                {activeTab === 'audit' && <AuditLogView />}
                {activeTab === 'identity' && <LoginView />}
            </main>
        </div>
    );
};


export const App: React.FC = () => {
    return (
        <AuthProvider>
            <MainAppContent />
        </AuthProvider>
    );
};

export default App;
