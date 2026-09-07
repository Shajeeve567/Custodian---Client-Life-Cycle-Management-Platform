import React, { useState } from 'react';
import { Link, useLocation } from 'react-router-dom';
import { useAuth } from '../context/AuthContext';
import { Shield, Building2, LogOut, Users, ExternalLink } from 'lucide-react';
import { TeamManagementModal } from './TeamManagementModal';

interface DashboardLayoutProps {
    children: React.ReactNode;
}

export const DashboardLayout: React.FC<DashboardLayoutProps> = ({ children }) => {
    const { tenantName, userId, email, role, logout } = useAuth();
    const location = useLocation();
    const [isTeamModalOpen, setIsTeamModalOpen] = useState(false);

    const agencyWorkspaceTitle = tenantName ? `${tenantName}'s Workspace` : 'Agency Workspace';

    return (
        <div className="min-h-screen bg-[#f8fafc] flex flex-col font-sans text-slate-900 relative overflow-x-hidden">
            {/* Luminous atmospheric dot background matching Landing and Auth */}
            <div className="landing-dot-bg opacity-40 pointer-events-none" aria-hidden="true" />
            <div className="dash-glow-top pointer-events-none" aria-hidden="true" />
            <div className="dash-glow-left pointer-events-none" aria-hidden="true" />
            <div className="dash-glow-right pointer-events-none" aria-hidden="true" />

            {/* Top Navigation Bar */}
            <header className="sticky top-0 z-40 bg-white/85 backdrop-blur-md border-b border-slate-200/80 shadow-xs">
                <div className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8">
                    <div className="flex items-center justify-between h-16">
                        {/* Brand & Agency Workspace Title */}
                        <div className="flex items-center gap-4">
                            <Link to="/engagements" className="flex items-center gap-3 text-decoration-none group">
                                <div className="w-9 h-9 rounded-xl bg-gradient-to-br from-[#635bff] to-[#712ae2] flex items-center justify-center text-white shadow-md shadow-indigo-500/20 group-hover:scale-105 transition-transform">
                                    <Shield className="w-5 h-5 text-white" />
                                </div>
                                <div className="flex flex-col">
                                    <div className="flex items-center gap-2">
                                        <span className="font-bold text-lg text-slate-900 tracking-tight leading-tight">
                                            {agencyWorkspaceTitle}
                                        </span>
                                        <span className="text-[10px] font-bold text-indigo-600 uppercase tracking-wider bg-indigo-50 px-2 py-0.5 rounded-full border border-indigo-100">
                                            Custodian OS
                                        </span>
                                    </div>
                                    <span className="text-[11px] text-slate-400 font-medium">
                                        Deterministic Client Isolation & Orchestration
                                    </span>
                                </div>
                            </Link>
                        </div>

                        {/* Right Area: Workspace Badge & User Profile */}
                        <div className="flex items-center gap-3">
                            {/* Current Workspace badge */}
                            <div className="hidden sm:flex items-center gap-2 px-3 py-1.5 rounded-lg bg-white/80 backdrop-blur-xs text-xs text-slate-700 border border-slate-200/80 shadow-xs">
                                <Building2 className="w-3.5 h-3.5 text-indigo-600" />
                                <span className="font-semibold truncate max-w-[170px]">
                                    {tenantName || 'Main Workspace'}
                                </span>
                            </div>

                            {/* User Avatar & Role */}
                            <div className="flex items-center gap-2 pl-2 border-l border-slate-200">
                                <div className="w-8 h-8 rounded-full bg-gradient-to-br from-[#635bff] to-[#712ae2] text-white text-xs font-bold flex items-center justify-center shadow-xs">
                                    {(email?.[0] || userId?.[0] || 'U').toUpperCase()}
                                </div>
                                <div className="hidden lg:flex flex-col text-left">
                                    <span className="text-xs font-semibold text-slate-800 truncate max-w-[140px]">
                                        {email?.split('@')[0] || 'Admin'}
                                    </span>
                                    <span className="text-[10px] font-bold text-indigo-600 uppercase tracking-wider">
                                        {role}
                                    </span>
                                </div>

                                <button
                                    onClick={logout}
                                    className="p-1.5 rounded-lg text-slate-400 hover:text-red-600 hover:bg-red-50 transition ml-1"
                                    title="Sign out"
                                >
                                    <LogOut className="w-4 h-4" />
                                </button>
                            </div>
                        </div>
                    </div>
                </div>
            </header>

            {/* Main Content Area */}
            <main className="flex-1 max-w-7xl w-full mx-auto px-4 sm:px-6 lg:px-8 py-8 relative z-10">
                {children}
            </main>

            {/* Footer */}
            <footer className="bg-white/80 backdrop-blur-md border-t border-slate-200/80 py-6 mt-auto relative z-10">
                <div className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 flex flex-col sm:flex-row items-center justify-between gap-3 text-xs text-slate-500">
                    <div className="flex items-center gap-2">
                        <span className="w-2 h-2 rounded-full bg-emerald-500 animate-pulse" />
                        <span className="font-medium">Custodian Agency OS • Deterministic Client Isolation Active</span>
                    </div>
                    <div className="flex items-center gap-6">
                        <Link to="/documents" className="hover:text-indigo-600 font-medium transition">Document Vault</Link>
                        <Link to="/audit" className="hover:text-indigo-600 font-medium transition">Audit Log</Link>
                        <span>© 2025 Custodian</span>
                    </div>
                </div>
            </footer>

            {/* Team Management Modal */}
            <TeamManagementModal
                isOpen={isTeamModalOpen}
                onClose={() => setIsTeamModalOpen(false)}
            />
        </div>
    );
};
