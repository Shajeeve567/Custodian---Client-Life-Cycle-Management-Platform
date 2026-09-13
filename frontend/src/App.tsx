import React from 'react';
import { BrowserRouter, Routes, Route, Navigate } from 'react-router-dom';
import { AuthProvider } from './context/AuthContext';
import { ProtectedRoute } from './components/ProtectedRoute';
import { LandingPage } from './pages/LandingPage';
import { AuthPage } from './pages/AuthPage';
import { EngagementsPage } from './pages/EngagementsPage';
import { WorkflowsPage } from './pages/WorkflowsPage';
import { DocumentsPage } from './pages/DocumentsPage';
import { AuditPage } from './pages/AuditPage';
import { PortalPage } from './pages/PortalPage';
import { WorkspacePage } from './pages/WorkspacePage';
import './App.css';

export const App: React.FC = () => {
    return (
        <AuthProvider>
            <BrowserRouter>
                <Routes>
                    {/* Public Home / Landing Page (Figma Node 9:862) */}
                    <Route path="/" element={<LandingPage />} />

                    {/* Authentication Flows (Figma Node 44:2) */}
                    <Route path="/login" element={<AuthPage initialMode="login" />} />
                    <Route path="/register" element={<AuthPage initialMode="register" />} />

                    {/* Protected Operational Routes */}
                    <Route element={<ProtectedRoute />}>
                        {/* Client Portal: Available to Clients (and Staff previewing) */}
                        <Route path="/portal" element={<PortalPage />} />

                        {/* Agency Operations: Strictly restricted to Staff, Owner, Admin */}
                        <Route element={<ProtectedRoute allowedRoles={['Owner', 'Staff', 'Admin']} />}>
                            <Route path="/engagements" element={<EngagementsPage />} />
                            <Route path="/workspace/:engagementId" element={<WorkspacePage />} />
                            <Route path="/actions" element={<WorkflowsPage />} />
                            <Route path="/workflows" element={<Navigate to="/actions" replace />} />
                            <Route path="/documents" element={<DocumentsPage />} />
                            <Route path="/audit" element={<AuditPage />} />
                        </Route>
                    </Route>

                    {/* Fallback */}
                    <Route path="*" element={<Navigate to="/" replace />} />
                </Routes>
            </BrowserRouter>
        </AuthProvider>
    );
};

export default App;
