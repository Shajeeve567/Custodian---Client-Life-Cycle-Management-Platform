import React, { useState } from 'react';
import { useAuth } from '../context/AuthContext';
import { UserRole } from '../types';
import './LoginView.css';

export const LoginView: React.FC = () => {
    const { token, userEmail, tenantId, role, selectWorkspace, login, register, logout, isAuthenticated } = useAuth();

    const [mode, setMode] = useState<'login' | 'register'>('login');
    const [email, setEmail] = useState('');
    const [password, setPassword] = useState('');
    const [regRole, setRegRole] = useState<UserRole>('Staff');
    const [targetTenant, setTargetTenant] = useState(tenantId);

    const [loading, setLoading] = useState(false);
    const [error, setError] = useState<string | null>(null);
    const [successMsg, setSuccessMsg] = useState<string | null>(null);

    const handleLogin = async (e: React.FormEvent) => {
        e.preventDefault();
        setError(null);
        setSuccessMsg(null);
        setLoading(true);
        try {
            await login({ email, password });
            setSuccessMsg('Successfully signed in!');
        } catch (err: any) {
            setError(err.message || 'Login failed.');
        } finally {
            setLoading(false);
        }
    };

    const handleRegister = async (e: React.FormEvent) => {
        e.preventDefault();
        setError(null);
        setSuccessMsg(null);
        setLoading(true);
        try {
            await register({ email, password, role: regRole });
            setSuccessMsg(`User ${email} registered successfully! You can now sign in.`);
            setMode('login');
        } catch (err: any) {
            setError(err.message || 'Registration failed.');
        } finally {
            setLoading(false);
        }
    };

    const handleWorkspaceSwitch = async (e: React.FormEvent) => {
        e.preventDefault();
        setError(null);
        setSuccessMsg(null);
        setLoading(true);
        try {
            await selectWorkspace(targetTenant);
            setSuccessMsg(`Switched workspace to ${targetTenant}`);
        } catch (err: any) {
            setError(err.message || 'Workspace switch failed.');
        } finally {
            setLoading(false);
        }
    };

    if (isAuthenticated) {
        return (
            <div className="login-container">
                <div className="auth-card">
                    <div className="auth-badge-header">
                        <span className="badge-active">Connected to Identity Service</span>
                    </div>
                    <h2>Authenticated User Profile</h2>

                    <div className="user-details">
                        <div className="detail-row">
                            <span className="label">User Email:</span>
                            <span className="value">{userEmail}</span>
                        </div>
                        <div className="detail-row">
                            <span className="label">Current Role:</span>
                            <span className="value role-badge">{role}</span>
                        </div>
                        <div className="detail-row">
                            <span className="label">Current Tenant Workspace:</span>
                            <span className="value tenant-badge">{tenantId}</span>
                        </div>
                        <div className="detail-row token-row">
                            <span className="label">JWT Token:</span>
                            <span className="value token-preview">{token?.slice(0, 25)}...</span>
                        </div>
                    </div>

                    <hr className="divider" />

                    <h3>Switch Workspace / Tenant</h3>
                    <form onSubmit={handleWorkspaceSwitch} className="workspace-form">
                        <div className="form-group">
                            <label>Target Workspace (Tenant ID)</label>
                            <input
                                type="text"
                                value={targetTenant}
                                onChange={(e) => setTargetTenant(e.target.value)}
                                placeholder="e.g. tenant-alpha or GUID"
                                required
                            />
                        </div>
                        <button type="submit" disabled={loading} className="btn-primary">
                            {loading ? 'Switching...' : 'Switch Workspace'}
                        </button>
                    </form>

                    {successMsg && <div className="alert alert-success">{successMsg}</div>}
                    {error && <div className="alert alert-error">{error}</div>}

                    <div className="auth-actions">
                        <button onClick={logout} className="btn-secondary btn-danger">
                            Sign Out
                        </button>
                    </div>
                </div>
            </div>
        );
    }

    return (
        <div className="login-container">
            <div className="auth-card">
                <div className="auth-header">
                    <h2>Custodian Identity Portal</h2>
                    <p className="subtitle">Secure Multi-Tenant Authentication & Access</p>
                </div>

                <div className="tab-switch">
                    <button
                        className={`tab-btn ${mode === 'login' ? 'active' : ''}`}
                        onClick={() => { setMode('login'); setError(null); setSuccessMsg(null); }}
                    >
                        Sign In
                    </button>
                    <button
                        className={`tab-btn ${mode === 'register' ? 'active' : ''}`}
                        onClick={() => { setMode('register'); setError(null); setSuccessMsg(null); }}
                    >
                        Register
                    </button>
                </div>

                {mode === 'login' ? (
                    <form onSubmit={handleLogin} className="auth-form">
                        <div className="form-group">
                            <label>Email Address</label>
                            <input
                                type="email"
                                value={email}
                                onChange={(e) => setEmail(e.target.value)}
                                placeholder="name@custodian.com"
                                required
                            />
                        </div>
                        <div className="form-group">
                            <label>Password</label>
                            <input
                                type="password"
                                value={password}
                                onChange={(e) => setPassword(e.target.value)}
                                placeholder="••••••••"
                                required
                            />
                        </div>

                        <button type="submit" disabled={loading} className="btn-primary full-width">
                            {loading ? 'Signing In...' : 'Sign In'}
                        </button>
                    </form>
                ) : (
                    <form onSubmit={handleRegister} className="auth-form">
                        <div className="form-group">
                            <label>Email Address</label>
                            <input
                                type="email"
                                value={email}
                                onChange={(e) => setEmail(e.target.value)}
                                placeholder="newuser@custodian.com"
                                required
                            />
                        </div>
                        <div className="form-group">
                            <label>Password</label>
                            <input
                                type="password"
                                value={password}
                                onChange={(e) => setPassword(e.target.value)}
                                placeholder="••••••••"
                                required
                            />
                        </div>
                        <div className="form-group">
                            <label>Role</label>
                            <select value={regRole} onChange={(e) => setRegRole(e.target.value as UserRole)}>
                                <option value="Staff">Staff</option>
                                <option value="Client">Client</option>
                                <option value="Admin">Admin / Owner</option>
                            </select>
                        </div>

                        <button type="submit" disabled={loading} className="btn-primary full-width">
                            {loading ? 'Creating Account...' : 'Register User'}
                        </button>
                    </form>
                )}

                {successMsg && <div className="alert alert-success">{successMsg}</div>}
                {error && <div className="alert alert-error">{error}</div>}
            </div>
        </div>
    );
};
