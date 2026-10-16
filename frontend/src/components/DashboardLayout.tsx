import React, { useEffect, useRef, useState } from 'react';
import { Link, useLocation } from 'react-router-dom';
import { useAuth } from '../context/AuthContext';
import { Building2, ChevronDown, LogOut, Shield } from 'lucide-react';
import { TeamManagementModal } from './TeamManagementModal';

const NAV_LINKS = [
    // An engagement's workspace (/workspace/...) belongs to the Engagements section.
    { to: '/engagements', label: 'Engagements', alsoActiveFor: '/workspace/' },
    { to: '/stall-queue', label: 'Stall Queue' },
    { to: '/actions', label: 'Action History' },
    { to: '/reports', label: 'Reports' },
];

const isActiveLink = (link: { to: string; alsoActiveFor?: string }, pathname: string) =>
    pathname === link.to || (link.alsoActiveFor !== undefined && pathname.startsWith(link.alsoActiveFor));

interface DashboardLayoutProps {
    children: React.ReactNode;
}

export const DashboardLayout: React.FC<DashboardLayoutProps> = ({ children }) => {
    const { tenantName, userId, email, role, logout } = useAuth();
    const location = useLocation();
    const [isTeamModalOpen, setIsTeamModalOpen] = useState(false);

    const [isMenuOpen, setIsMenuOpen] = useState(false);
    const menuRef = useRef<HTMLDivElement>(null);

    const workspaceLine = role === 'Client'
        ? (tenantName ? `${tenantName} · Client Portal` : 'Client Portal')
        : (tenantName || 'Agency workspace');
    const displayName = email?.split('@')[0] || (role === 'Client' ? 'Client' : 'Staff');

    // Close the account menu on navigation, outside clicks and Escape.
    useEffect(() => setIsMenuOpen(false), [location.pathname]);
    useEffect(() => {
        if (!isMenuOpen) return;
        const onPointerDown = (e: MouseEvent) => {
            if (menuRef.current && !menuRef.current.contains(e.target as Node)) setIsMenuOpen(false);
        };
        const onKeyDown = (e: KeyboardEvent) => {
            if (e.key === 'Escape') setIsMenuOpen(false);
        };
        document.addEventListener('mousedown', onPointerDown);
        document.addEventListener('keydown', onKeyDown);
        return () => {
            document.removeEventListener('mousedown', onPointerDown);
            document.removeEventListener('keydown', onKeyDown);
        };
    }, [isMenuOpen]);

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
                    <div className="flex items-center justify-between gap-4 h-16">
                        {/* Brand: workspace name */}
                        <Link
                            to={role === 'Client' ? '/portal' : '/engagements'}
                            className="flex items-center gap-3 min-w-0 text-decoration-none group"
                        >
                            <div className="w-9 h-9 shrink-0 rounded-xl bg-gradient-to-br from-[#635bff] to-[#712ae2] flex items-center justify-center text-white shadow-md shadow-indigo-500/20 group-hover:scale-105 transition-transform">
                                <Shield className="w-5 h-5 text-white" />
                            </div>
                            {/* Hidden on narrower screens so the workspace name always fits in full; the logo stays. */}
                            <span className="hidden lg:inline font-bold text-lg text-slate-900 tracking-tight leading-tight shrink-0">Custodian</span>
                            {/* Workspace pill: purple-tinted so it reads as "where you are", not as another nav button */}
                            <span
                                className="inline-flex items-center gap-1.5 min-w-0 max-w-[200px] sm:max-w-[240px] px-2.5 py-1 rounded-lg border border-purple-200 bg-gradient-to-r from-indigo-50 to-purple-50 text-purple-700 text-xs font-semibold"
                                title={workspaceLine}
                            >
                                <Building2 className="w-3.5 h-3.5 shrink-0" aria-hidden="true" />
                                <span className="truncate">{workspaceLine}</span>
                            </span>
                        </Link>

                        <div className="flex items-center gap-3 shrink-0">
                            {/* Main pages (staff); on small screens they move into the account menu */}
                            {role !== 'Client' && (
                                <nav className="hidden md:flex items-center gap-2" aria-label="Main">
                                    {NAV_LINKS.map((link) => (
                                        <Link
                                            key={link.to}
                                            to={link.to}
                                            aria-current={isActiveLink(link, location.pathname) ? 'page' : undefined}
                                            className={`px-3 py-1.5 rounded-lg border text-xs font-semibold whitespace-nowrap transition ${
                                                isActiveLink(link, location.pathname)
                                                    ? 'border-indigo-300 bg-indigo-50 text-indigo-700'
                                                    : 'border-slate-200 hover:bg-slate-50 text-slate-600'
                                            }`}
                                        >
                                            {link.label}
                                        </Link>
                                    ))}
                                </nav>
                            )}

                            {/* Account menu: who is signed in, and sign out */}
                            <div className="relative pl-3 border-l border-slate-200" ref={menuRef}>
                                <button
                                    type="button"
                                    onClick={() => setIsMenuOpen((open) => !open)}
                                    aria-haspopup="menu"
                                    aria-expanded={isMenuOpen}
                                    aria-label="Account menu"
                                    className="flex items-center gap-2 p-1 rounded-xl hover:bg-slate-50 transition focus:outline-none focus-visible:ring-2 focus-visible:ring-indigo-500/40"
                                >
                                    <span className="w-8 h-8 rounded-full bg-gradient-to-br from-[#635bff] to-[#712ae2] text-white text-xs font-bold flex items-center justify-center shadow-xs">
                                        {(email?.[0] || userId?.[0] || 'U').toUpperCase()}
                                    </span>
                                    <span className="hidden lg:flex flex-col text-left">
                                        <span className="text-xs font-semibold text-slate-800 truncate max-w-[140px]">{displayName}</span>
                                        <span className="text-[10px] font-bold text-indigo-600 uppercase tracking-wider">{role}</span>
                                    </span>
                                    <ChevronDown className={`w-4 h-4 text-slate-400 transition-transform ${isMenuOpen ? 'rotate-180' : ''}`} />
                                </button>

                                {isMenuOpen && (
                                    <div
                                        role="menu"
                                        className="absolute right-0 mt-2 w-60 bg-white rounded-xl border border-slate-200 shadow-lg py-1.5 z-50"
                                    >
                                        <div className="px-3.5 py-2 border-b border-slate-100">
                                            <div className="text-xs font-semibold text-slate-800 truncate">{email || displayName}</div>
                                            <div className="text-[11px] text-slate-500 truncate">
                                                {role}{tenantName ? ` · ${tenantName}` : ''}
                                            </div>
                                        </div>
                                        {role !== 'Client' && (
                                            <div className="md:hidden py-1 border-b border-slate-100">
                                                {NAV_LINKS.map((link) => (
                                                    <Link
                                                        key={link.to}
                                                        to={link.to}
                                                        role="menuitem"
                                                        onClick={() => setIsMenuOpen(false)}
                                                        className={`block px-3.5 py-2 text-sm transition ${
                                                            isActiveLink(link, location.pathname) ? 'text-indigo-700 font-semibold bg-indigo-50/60' : 'text-slate-700 hover:bg-slate-50'
                                                        }`}
                                                    >
                                                        {link.label}
                                                    </Link>
                                                ))}
                                            </div>
                                        )}
                                        <button
                                            type="button"
                                            role="menuitem"
                                            onClick={logout}
                                            className="w-full flex items-center gap-2 px-3.5 py-2 text-sm text-slate-700 hover:bg-rose-50 hover:text-rose-700 transition"
                                        >
                                            <LogOut className="w-4 h-4" />
                                            Sign out
                                        </button>
                                    </div>
                                )}
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
                        <span className="font-medium">
                            {role === 'Client'
                                ? 'Custodian Client Portal • Deterministic Engagement Security Active'
                                : 'Custodian Agency OS • Deterministic Client Isolation Active'}
                        </span>
                    </div>
                    <div className="flex items-center gap-6">
                        {role !== 'Client' && (
                            <>
                                <Link to="/stall-queue" className="hover:text-indigo-600 font-medium transition">Stall Queue</Link>
                                <Link to="/actions" className="hover:text-indigo-600 font-medium transition">Action History</Link>
                                <Link to="/documents" className="hover:text-indigo-600 font-medium transition">Document Vault</Link>
                                <Link to="/audit" className="hover:text-indigo-600 font-medium transition">Audit Log</Link>
                                <Link to="/reports" className="hover:text-indigo-600 font-medium transition">Reports</Link>
                            </>
                        )}
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
