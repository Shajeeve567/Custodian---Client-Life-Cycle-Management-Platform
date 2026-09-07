import React, { useState, useEffect } from 'react';
import { useNavigate, useLocation } from 'react-router-dom';
import { useAuth } from '../context/AuthContext';
import { IdentityApi, ApiError } from '../services/api';
import { TenantMembership } from '../types';
import {
    Shield,
    Lock,
    Mail,
    Building2,
    Eye,
    EyeOff,
    ArrowRight,
    Loader2
} from 'lucide-react';

interface AuthPageProps {
    initialMode?: 'login' | 'register';
}

export const AuthPage: React.FC<AuthPageProps> = ({ initialMode = 'login' }) => {
    const navigate = useNavigate();
    const location = useLocation();
    const { isAuthenticated, setWorkspaceToken } = useAuth();

    // Determine mode from prop or path
    const [mode, setMode] = useState<'login' | 'register'>(
        initialMode || (location.pathname === '/register' ? 'register' : 'login')
    );

    useEffect(() => {
        if (location.pathname === '/register') {
            setMode('register');
        } else if (location.pathname === '/login') {
            setMode('login');
        }
    }, [location.pathname]);

    // If already authenticated with workspace token, redirect to /engagements
    useEffect(() => {
        if (isAuthenticated) {
            navigate('/engagements', { replace: true });
        }
    }, [isAuthenticated, navigate]);

    // Form inputs
    const [email, setEmail] = useState('');
    const [password, setPassword] = useState('');
    const [showPassword, setShowPassword] = useState(false);

    // States for multi-step / workspace picker
    const [isSubmitting, setIsSubmitting] = useState(false);
    const [statusMessage, setStatusMessage] = useState<string | null>(null);
    const [errorMessage, setErrorMessage] = useState<string | null>(null);

    // Workspace selection state for authenticated users (Global Token active)
    const [globalToken, setGlobalToken] = useState<string | null>(null);
    const [availableWorkspaces, setAvailableWorkspaces] = useState<TenantMembership[]>([]);
    const [showIntermediateHub, setShowIntermediateHub] = useState(false);
    const [showCreateWorkspaceForm, setShowCreateWorkspaceForm] = useState(false);
    const [newFirmName, setNewFirmName] = useState('');

    const switchMode = (newMode: 'login' | 'register') => {
        setErrorMessage(null);
        setStatusMessage(null);
        setShowIntermediateHub(false);
        setShowCreateWorkspaceForm(false);
        setMode(newMode);
        navigate(newMode === 'register' ? '/register' : '/login');
    };

    // Query available workspaces and transition to intermediate hub
    const proceedToWorkspaceHub = async (gToken: string) => {
        setStatusMessage('Discovering authorized agency workspaces & invites...');
        try {
            const workspaces = await IdentityApi.getMyTenants(gToken);
            setAvailableWorkspaces(workspaces || []);
            setShowIntermediateHub(true);
            // If they have no workspaces, auto-show the create workspace form
            setShowCreateWorkspaceForm(!workspaces || workspaces.length === 0);
        } catch (err: any) {
            console.error('Failed to load workspaces:', err);
            setAvailableWorkspaces([]);
            setShowIntermediateHub(true);
            setShowCreateWorkspaceForm(true);
        }
    };

    // ==========================================
    // Register Flow (No Workspace Field Upfront!)
    // ==========================================
    const handleRegister = async (e: React.FormEvent) => {
        e.preventDefault();
        setErrorMessage(null);
        setStatusMessage(null);

        if (!email.trim() || !password.trim()) {
            setErrorMessage('Please provide both work email and security password.');
            return;
        }

        setIsSubmitting(true);
        try {
            // 1. Create global user account
            setStatusMessage('Creating verified identity node...');
            await IdentityApi.register(email.trim(), password);

            // 2. Login to obtain Global Token
            setStatusMessage('Acquiring security token...');
            const loginRes = await IdentityApi.login(email.trim(), password);
            const gToken = loginRes.token;
            setGlobalToken(gToken);

            // 3. Move to Intermediate Workspace Hub
            await proceedToWorkspaceHub(gToken);
        } catch (err: any) {
            console.error('Registration failed:', err);
            if (err instanceof ApiError) {
                setErrorMessage(err.message);
            } else {
                setErrorMessage(err?.message || 'Registration failed. Please try again.');
            }
        } finally {
            setIsSubmitting(false);
            setStatusMessage(null);
        }
    };

    // ==========================================
    // Login Flow
    // ==========================================
    const handleLogin = async (e: React.FormEvent) => {
        e.preventDefault();
        setErrorMessage(null);
        setStatusMessage(null);

        if (!email.trim() || !password.trim()) {
            setErrorMessage('Please enter both email and password.');
            return;
        }

        setIsSubmitting(true);
        try {
            // 1. Authenticate & obtain Global Token
            setStatusMessage('Authenticating security credentials...');
            const loginRes = await IdentityApi.login(email.trim(), password);
            const gToken = loginRes.token;
            setGlobalToken(gToken);

            // 2. Query user's workspaces
            setStatusMessage('Discovering authorized workspaces...');
            const workspaces = await IdentityApi.getMyTenants(gToken);

            if (workspaces && workspaces.length === 1) {
                // Exactly 1 workspace: Auto-select it directly
                const target = workspaces[0];
                setStatusMessage(`Connecting to workspace "${target.name}"...`);
                const wsRes = await IdentityApi.selectWorkspace(target.tenantId, gToken);
                setWorkspaceToken(wsRes.token, target.tenantId, target.name);
                navigate('/engagements');
            } else {
                // 0 workspaces (needs creation) OR >1 workspaces (needs selection)
                setAvailableWorkspaces(workspaces || []);
                setShowIntermediateHub(true);
                setShowCreateWorkspaceForm(!workspaces || workspaces.length === 0);
            }
        } catch (err: any) {
            console.error('Login failed:', err);
            if (err instanceof ApiError) {
                setErrorMessage(err.message);
            } else {
                setErrorMessage(err?.message || 'Authentication failed. Please verify your credentials.');
            }
        } finally {
            setIsSubmitting(false);
            setStatusMessage(null);
        }
    };

    // ==========================================
    // Workspace Selection (Staff or Owner entering workspace)
    // ==========================================
    const handleSelectWorkspace = async (workspace: TenantMembership) => {
        if (!globalToken) return;
        setIsSubmitting(true);
        setErrorMessage(null);
        try {
            setStatusMessage(`Entering "${workspace.name}"...`);
            const wsRes = await IdentityApi.selectWorkspace(workspace.tenantId, globalToken);
            setWorkspaceToken(wsRes.token, workspace.tenantId, workspace.name);
            navigate('/engagements');
        } catch (err: any) {
            console.error('Workspace selection failed:', err);
            setErrorMessage(err?.message || 'Failed to enter workspace.');
        } finally {
            setIsSubmitting(false);
            setStatusMessage(null);
        }
    };

    // ==========================================
    // Owner creates an Agency Workspace
    // ==========================================
    const handleCreateAgencyWorkspace = async (e: React.FormEvent) => {
        e.preventDefault();
        if (!globalToken) return;
        if (!newFirmName.trim()) {
            setErrorMessage('Please enter an agency workspace name.');
            return;
        }

        setIsSubmitting(true);
        setErrorMessage(null);
        try {
            setStatusMessage(`Initializing agency workspace "${newFirmName.trim()}"...`);
            const tenant = await IdentityApi.createTenant(newFirmName.trim(), globalToken);

            setStatusMessage('Establishing operational session...');
            const wsRes = await IdentityApi.selectWorkspace(tenant.id, globalToken);
            setWorkspaceToken(wsRes.token, tenant.id, tenant.name);
            navigate('/engagements');
        } catch (err: any) {
            console.error('Creating workspace failed:', err);
            setErrorMessage(err?.message || 'Failed to initialize agency workspace.');
        } finally {
            setIsSubmitting(false);
            setStatusMessage(null);
        }
    };

    return (
        <div className="auth-container">
            {/* Luminous Ambient Background Glows & Dot Grid */}
            <div className="auth-ambient-glow auth-glow-1" aria-hidden="true" />
            <div className="auth-ambient-glow auth-glow-2" aria-hidden="true" />
            <div className="auth-ambient-glow auth-glow-3" aria-hidden="true" />
            <div className="auth-dot-grid" aria-hidden="true" />

            {/* Central Card */}
            <div className="auth-card-wrapper" style={{ maxWidth: showIntermediateHub ? '560px' : '500px' }}>
                <div className="auth-card-glow" aria-hidden="true" />

                <div className="auth-card">
                    {/* Header & Brand Telemetry */}
                    <div className="auth-header">
                        <div className="auth-logo-badge">
                            <div className="auth-logo-icon">
                                <Shield className="w-6 h-6 text-white" />
                            </div>
                        </div>

                        <div className="telemetry-pill">
                            <span className="telemetry-dot" />
                            <span className="telemetry-text">
                                {showIntermediateHub ? 'WORKSPACE RESOLUTION NODE' : 'SECURE SYSTEM NODE 01'}
                            </span>
                        </div>

                        <h1 className="auth-title">Custodian</h1>
                        <p className="auth-subtitle">
                            {showIntermediateHub
                                ? 'Select an authorized workspace or initialize a new agency'
                                : 'Deterministic workflow control for digital agencies'}
                        </p>
                    </div>

                    {/* Conditional Views */}
                    {showIntermediateHub ? (
                        /* Intermediate Hub: For Staff & Owners */
                        <div className="space-y-4">
                            {errorMessage && <div className="auth-error-banner">{errorMessage}</div>}
                            {statusMessage && <div className="auth-status-banner">{statusMessage}</div>}

                            {/* Section 1: Authorized Workspaces / Invites Found */}
                            {availableWorkspaces.length > 0 && (
                                <div className="space-y-3">
                                    <div className="flex items-center justify-between pb-1 border-b border-slate-100">
                                        <h2 className="text-xs font-bold text-slate-700 tracking-wider uppercase flex items-center gap-1.5">
                                            <Building2 className="w-4 h-4 text-indigo-600" />
                                            <span>Your Authorized Workspaces ({availableWorkspaces.length})</span>
                                        </h2>
                                        <span className="text-[11px] text-slate-400">Click to enter</span>
                                    </div>

                                    <div className="space-y-2">
                                        {availableWorkspaces.map((ws) => {
                                            const roleStr = String(ws.role);
                                            const isOwner = roleStr === 'Owner' || roleStr === '0';
                                            return (
                                                <button
                                                    key={ws.tenantId}
                                                    onClick={() => handleSelectWorkspace(ws)}
                                                    disabled={isSubmitting}
                                                    className="w-full p-3.5 bg-white hover:bg-slate-50 border border-slate-200 hover:border-indigo-300 rounded-xl transition flex items-center justify-between group shadow-xs cursor-pointer text-left"
                                                >
                                                    <div className="flex items-center gap-3">
                                                        <div className="w-9 h-9 rounded-lg bg-gradient-to-br from-[#635bff] to-[#712ae2] text-white flex items-center justify-center font-bold text-xs shadow-xs group-hover:scale-105 transition">
                                                            {ws.name.slice(0, 2).toUpperCase()}
                                                        </div>
                                                        <div>
                                                            <div className="font-bold text-slate-900 text-sm group-hover:text-indigo-600 transition">
                                                                {ws.name}
                                                            </div>
                                                            <div className="flex items-center gap-2 mt-0.5">
                                                                <span className={`text-[10px] font-bold uppercase tracking-wider px-2 py-0.5 rounded-full ${
                                                                    isOwner
                                                                        ? 'bg-purple-100 text-purple-700 border border-purple-200'
                                                                        : 'bg-indigo-100 text-indigo-700 border border-indigo-200'
                                                                }`}>
                                                                    {isOwner ? 'Owner' : 'Staff'}
                                                                </span>
                                                                <span className="text-[11px] text-slate-400">
                                                                    Joined {new Date(ws.createdAtUtc).toLocaleDateString()}
                                                                </span>
                                                            </div>
                                                        </div>
                                                    </div>
                                                    <div className="flex items-center gap-1 text-xs font-semibold text-indigo-600 group-hover:translate-x-0.5 transition">
                                                        <span>Enter</span>
                                                        <ArrowRight className="w-3.5 h-3.5" />
                                                    </div>
                                                </button>
                                            );
                                        })}
                                    </div>
                                </div>
                            )}

                            {/* Section 2: Create Agency Workspace (For Owners without workspace or wanting a new one) */}
                            {showCreateWorkspaceForm ? (
                                <form onSubmit={handleCreateAgencyWorkspace} className="bg-slate-50/80 border border-slate-200/80 rounded-xl p-4 space-y-3 mt-2">
                                    <div className="flex items-center justify-between pb-1 border-b border-slate-200/60">
                                        <h3 className="text-xs font-bold text-slate-800 uppercase tracking-wider flex items-center gap-1.5">
                                            <Building2 className="w-4 h-4 text-indigo-600" />
                                            <span>Initialize Agency Workspace</span>
                                        </h3>
                                        {availableWorkspaces.length > 0 && (
                                            <button
                                                type="button"
                                                onClick={() => setShowCreateWorkspaceForm(false)}
                                                className="text-xs text-slate-500 hover:text-slate-800"
                                            >
                                                Cancel
                                            </button>
                                        )}
                                    </div>

                                    <p className="text-xs text-slate-500">
                                        {availableWorkspaces.length === 0
                                            ? 'You do not have an active workspace or invite. If you are an agency lead, name and initialize your workspace below:'
                                            : 'Create an additional dedicated workspace for another agency brand:'}
                                    </p>

                                    <div className="auth-field">
                                        <label className="auth-field-label">AGENCY WORKSPACE NAME</label>
                                        <div className="auth-input-wrapper">
                                            <Building2 className="auth-input-icon" />
                                            <input
                                                type="text"
                                                placeholder="e.g. Apex Digital Advisory"
                                                value={newFirmName}
                                                onChange={(e) => setNewFirmName(e.target.value)}
                                                className="auth-input"
                                                required
                                                disabled={isSubmitting}
                                            />
                                        </div>
                                    </div>

                                    <button
                                        type="submit"
                                        disabled={isSubmitting}
                                        className="auth-submit-btn"
                                    >
                                        {isSubmitting ? (
                                            <>
                                                <Loader2 className="w-4 h-4 animate-spin mr-2" />
                                                INITIALIZING WORKSPACE...
                                            </>
                                        ) : (
                                            <>
                                                <span>CREATE AGENCY WORKSPACE</span>
                                                <ArrowRight className="w-3.5 h-3.5 ml-1.5" />
                                            </>
                                        )}
                                    </button>
                                </form>
                            ) : (
                                <div className="pt-2 border-t border-slate-100 flex items-center justify-between">
                                    <span className="text-xs text-slate-500">Want to launch a new agency workspace?</span>
                                    <button
                                        type="button"
                                        onClick={() => setShowCreateWorkspaceForm(true)}
                                        className="text-xs font-semibold text-indigo-600 hover:underline inline-flex items-center gap-1"
                                    >
                                        <span>+ Create New Workspace</span>
                                    </button>
                                </div>
                            )}

                            {/* Back to sign in / switch account */}
                            <div className="pt-3 border-t border-slate-100 text-center">
                                <button
                                    type="button"
                                    onClick={() => switchMode('login')}
                                    className="text-xs font-medium text-slate-500 hover:text-slate-800 transition"
                                >
                                    Sign in with a different account
                                </button>
                            </div>
                        </div>
                    ) : (
                        /* Standard Login / Register Form */
                        <>
                            {/* Segmented Mode Switcher Tab */}
                            <div className="segmented-switcher">
                                <button
                                    type="button"
                                    onClick={() => switchMode('login')}
                                    className={`segmented-tab ${mode === 'login' ? 'segmented-tab-active' : ''}`}
                                >
                                    Sign In
                                </button>
                                <button
                                    type="button"
                                    onClick={() => switchMode('register')}
                                    className={`segmented-tab ${mode === 'register' ? 'segmented-tab-active' : ''}`}
                                >
                                    Create Account
                                </button>
                            </div>

                            {errorMessage && <div className="auth-error-banner">{errorMessage}</div>}
                            {statusMessage && <div className="auth-status-banner">{statusMessage}</div>}

                            <form onSubmit={mode === 'login' ? handleLogin : handleRegister} className="auth-form">
                                {/* Email Field */}
                                <div className="auth-field">
                                    <div className="auth-field-header">
                                        <label className="auth-field-label">WORK EMAIL</label>
                                        <span className="auth-field-sub">verified identity</span>
                                    </div>
                                    <div className="auth-input-wrapper">
                                        <Mail className="auth-input-icon" />
                                        <input
                                            type="email"
                                            placeholder="sarah@agency.io"
                                            value={email}
                                            onChange={(e) => setEmail(e.target.value)}
                                            className="auth-input"
                                            required
                                            disabled={isSubmitting}
                                        />
                                    </div>
                                </div>

                                {/* Password Field */}
                                <div className="auth-field">
                                    <div className="auth-field-header">
                                        <label className="auth-field-label">SECURITY CREDENTIAL</label>
                                        {mode === 'login' && (
                                            <span className="auth-field-link">Forgot password?</span>
                                        )}
                                    </div>
                                    <div className="auth-input-wrapper">
                                        <Lock className="auth-input-icon" />
                                        <input
                                            type={showPassword ? 'text' : 'password'}
                                            placeholder="••••••••••••••••"
                                            value={password}
                                            onChange={(e) => setPassword(e.target.value)}
                                            className="auth-input"
                                            required
                                            disabled={isSubmitting}
                                        />
                                        <button
                                            type="button"
                                            onClick={() => setShowPassword(!showPassword)}
                                            className="auth-input-action"
                                            tabIndex={-1}
                                        >
                                            {showPassword ? <EyeOff className="w-4 h-4" /> : <Eye className="w-4 h-4" />}
                                        </button>
                                    </div>
                                </div>

                                {/* Submit Button */}
                                <button
                                    type="submit"
                                    disabled={isSubmitting}
                                    className="auth-submit-btn"
                                >
                                    {isSubmitting ? (
                                        <>
                                            <Loader2 className="w-4 h-4 animate-spin mr-2" />
                                            {mode === 'login' ? 'AUTHENTICATING...' : 'CREATING ACCOUNT...'}
                                        </>
                                    ) : (
                                        <>
                                            <span>{mode === 'login' ? 'SIGN IN TO WORKSPACE' : 'CREATE ACCOUNT & CONTINUE'}</span>
                                            <ArrowRight className="w-3.5 h-3.5 ml-1.5" />
                                        </>
                                    )}
                                </button>
                            </form>
                        </>
                    )}

                    {/* SSO Divider */}
                    <div className="auth-divider">
                        <div className="auth-divider-line" />
                        <span className="auth-divider-text">ENTERPRISE IDENTITY SSO</span>
                    </div>
                </div>


                {/* Footer Micro-links */}
                <div className="auth-footer-links">
                    <span>Privacy Paradigm</span>
                    <span className="dot-sep">•</span>
                    <span>Service Terms</span>
                    <span className="dot-sep">•</span>
                    <span>Incident Status</span>
                </div>
            </div>
        </div>
    );
};
