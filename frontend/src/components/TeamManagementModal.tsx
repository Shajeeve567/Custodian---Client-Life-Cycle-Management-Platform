import React, { useState, useEffect, useCallback } from 'react';
import { useAuth } from '../context/AuthContext';
import { IdentityApi, ApiError } from '../services/api';
import { UserAccountResponse, UserRole } from '../types';
import {
    Users,
    UserPlus,
    X,
    Mail,
    Lock,
    Eye,
    EyeOff,
    CheckCircle2,
    AlertCircle,
    Loader2,
    Shield,
    Calendar,
    Copy,
    Check
} from 'lucide-react';

interface TeamManagementModalProps {
    isOpen: boolean;
    onClose: () => void;
}

export const TeamManagementModal: React.FC<TeamManagementModalProps> = ({ isOpen, onClose }) => {
    const { token, tenantId, tenantName, role } = useAuth();

    const [users, setUsers] = useState<UserAccountResponse[]>([]);
    const [isLoading, setIsLoading] = useState(false);
    const [generalError, setGeneralError] = useState<string | null>(null);

    // Form inputs
    const [email, setEmail] = useState('');
    const [password, setPassword] = useState('');
    const [showPassword, setShowPassword] = useState(false);
    const [assignedRole, setAssignedRole] = useState<UserRole>('Staff');
    const [isSubmitting, setIsSubmitting] = useState(false);
    const [submitError, setSubmitError] = useState<string | null>(null);
    const [provisionedCredentials, setProvisionedCredentials] = useState<{
        email: string;
        pass: string;
        role: string;
    } | null>(null);
    const [copied, setCopied] = useState(false);

    // Load team roster
    const loadTeam = useCallback(async () => {
        if (!token || role !== 'Owner') return;
        setIsLoading(true);
        setGeneralError(null);
        try {
            const data = await IdentityApi.getUsers(token);
            setUsers(data);
        } catch (err: any) {
            console.error('Failed to load team roster:', err);
            setGeneralError(err?.message || 'Failed to load team accounts.');
        } finally {
            setIsLoading(false);
        }
    }, [token, role]);

    useEffect(() => {
        if (isOpen && role === 'Owner') {
            loadTeam();
        }
    }, [isOpen, role, loadTeam]);

    if (!isOpen || role !== 'Owner') return null;

    const handleInvite = async (e: React.FormEvent) => {
        e.preventDefault();
        setSubmitError(null);
        setProvisionedCredentials(null);

        if (!email.trim() || !password.trim()) {
            setSubmitError('Work email and initial password are required.');
            return;
        }

        setIsSubmitting(true);
        try {
            await IdentityApi.inviteUser(
                {
                    email: email.trim(),
                    password: password.trim(),
                    role: assignedRole,
                },
                token || undefined
            );

            // Save credentials for the owner to copy/share
            setProvisionedCredentials({
                email: email.trim(),
                pass: password.trim(),
                role: assignedRole,
            });

            // Reset form
            setEmail('');
            setPassword('');

            // Reload team list
            loadTeam();
        } catch (err: any) {
            console.error('Failed to invite staff:', err);
            if (err instanceof ApiError) {
                setSubmitError(err.message);
            } else {
                setSubmitError(err?.message || 'Failed to invite staff account.');
            }
        } finally {
            setIsSubmitting(false);
        }
    };

    const copyCredentials = () => {
        if (!provisionedCredentials) return;
        const text = `Custodian Workspace Login:\nWorkspace: ${tenantName || 'Current'}\nEmail: ${provisionedCredentials.email}\nTemporary Password: ${provisionedCredentials.pass}\nRole: ${provisionedCredentials.role}\nLogin URL: ${window.location.origin}/login`;
        navigator.clipboard.writeText(text);
        setCopied(true);
        setTimeout(() => setCopied(false), 2000);
    };

    return (
        <div className="fixed inset-0 z-50 flex items-center justify-center p-4 bg-slate-950/40 backdrop-blur-sm">
            <div className="fixed inset-0" onClick={onClose} />
            <div className="relative z-10 bg-white/95 backdrop-blur-xl rounded-2xl shadow-2xl border border-slate-200/90 max-w-2xl w-full p-6 space-y-5" style={{ maxWidth: '680px' }}>
                <div className="flex items-start justify-between pb-3 border-b border-slate-100">
                    <div>
                        <div className="telemetry-pill mb-1">
                            <span className="telemetry-dot" />
                            <span className="telemetry-text">TENANT ACCESS CONTROL</span>
                        </div>
                        <h2 className="text-xl font-bold text-slate-900 flex items-center gap-2">
                            <Users className="w-5 h-5 text-indigo-600" />
                            <span>Team & Staff Management</span>
                        </h2>
                        <p className="text-xs text-slate-500 mt-0.5">
                            Workspace: <strong className="text-slate-800">{tenantName ? `${tenantName}'s Workspace` : 'Active Workspace'}</strong>
                        </p>
                    </div>
                    <button onClick={onClose} className="p-2 rounded-lg text-slate-400 hover:text-slate-700 hover:bg-slate-100 transition">
                        <X className="w-5 h-5" />
                    </button>
                </div>

                {/* Provisioned Success Banner */}
                {provisionedCredentials && (
                    <div className="bg-emerald-50 border border-emerald-200 rounded-xl p-3 text-xs space-y-2">
                        <div className="flex items-center justify-between font-semibold text-emerald-900">
                            <span className="flex items-center gap-1.5">
                                <CheckCircle2 className="w-4 h-4 text-emerald-600" />
                                Staff Account Successfully Provisioned!
                            </span>
                            <button
                                onClick={copyCredentials}
                                className="flex items-center gap-1 text-emerald-700 hover:text-emerald-900 bg-white border border-emerald-300 px-2.5 py-1 rounded-lg shadow-xs transition"
                            >
                                {copied ? <Check className="w-3.5 h-3.5 text-emerald-600" /> : <Copy className="w-3.5 h-3.5" />}
                                <span>{copied ? 'Copied!' : 'Copy Credentials'}</span>
                            </button>
                        </div>
                        <p className="text-emerald-700">
                            Share these credentials with your team member. They can log in immediately at <code className="bg-white px-1 py-0.5 rounded border border-emerald-200">/login</code>:
                        </p>
                        <div className="font-mono bg-white p-2.5 rounded-lg border border-emerald-200 text-slate-800 space-y-0.5 text-[11px]">
                            <div><strong>Email:</strong> {provisionedCredentials.email}</div>
                            <div><strong>Temporary Password:</strong> {provisionedCredentials.pass}</div>
                            <div><strong>Assigned Role:</strong> {provisionedCredentials.role}</div>
                        </div>
                    </div>
                )}

                {/* Invite Staff Section */}
                <div className="bg-slate-50/80 border border-slate-200/80 rounded-xl p-4 space-y-3">
                    <h3 className="text-xs font-bold text-slate-700 uppercase tracking-wider flex items-center gap-1.5">
                        <UserPlus className="w-4 h-4 text-indigo-600" />
                        <span>Invite / Provision Staff Member</span>
                    </h3>

                    {submitError && (
                        <div className="p-3 bg-red-50 border border-red-200 rounded-xl text-xs text-red-700 flex items-center gap-1.5">
                            <AlertCircle className="w-4 h-4 shrink-0" />
                            <span>{submitError}</span>
                        </div>
                    )}

                    <form onSubmit={handleInvite} className="grid grid-cols-1 md:grid-cols-3 gap-2.5">
                        <div className="relative flex items-center md:col-span-1">
                            <Mail className="absolute left-3 w-4 h-4 text-slate-400 pointer-events-none" />
                            <input
                                type="email"
                                placeholder="staff@agency.com"
                                value={email}
                                onChange={(e) => setEmail(e.target.value)}
                                className="w-full h-10 pl-9 pr-3 rounded-lg border border-slate-200 bg-white text-xs text-slate-800 placeholder:text-slate-400 focus:outline-none focus:ring-2 focus:ring-indigo-500/20 focus:border-indigo-500 transition"
                                required
                                disabled={isSubmitting}
                            />
                        </div>

                        <div className="relative flex items-center md:col-span-1">
                            <Lock className="absolute left-3 w-4 h-4 text-slate-400 pointer-events-none" />
                            <input
                                type={showPassword ? 'text' : 'password'}
                                placeholder="Initial Password"
                                value={password}
                                onChange={(e) => setPassword(e.target.value)}
                                className="w-full h-10 pl-9 pr-8 rounded-lg border border-slate-200 bg-white text-xs text-slate-800 placeholder:text-slate-400 focus:outline-none focus:ring-2 focus:ring-indigo-500/20 focus:border-indigo-500 transition"
                                required
                                disabled={isSubmitting}
                            />
                            <button
                                type="button"
                                onClick={() => setShowPassword(!showPassword)}
                                className="absolute right-2.5 text-slate-400 hover:text-slate-700"
                                tabIndex={-1}
                            >
                                {showPassword ? <EyeOff className="w-4 h-4" /> : <Eye className="w-4 h-4" />}
                            </button>
                        </div>

                        <div className="flex gap-2 md:col-span-1">
                            <select
                                value={assignedRole}
                                onChange={(e) => setAssignedRole(e.target.value as UserRole)}
                                className="h-10 px-3 rounded-lg border border-slate-200 bg-white text-xs text-slate-800 focus:outline-none focus:ring-2 focus:ring-indigo-500/20 focus:border-indigo-500 transition flex-1"
                                disabled={isSubmitting}
                            >
                                <option value="Staff">Staff</option>
                                <option value="Owner">Owner</option>
                            </select>

                            <button
                                type="submit"
                                disabled={isSubmitting}
                                className="h-10 px-4 rounded-lg bg-gradient-to-r from-[#635bff] to-[#712ae2] hover:opacity-95 text-white text-xs font-semibold shadow-xs shadow-indigo-500/25 transition shrink-0 whitespace-nowrap flex items-center justify-center gap-1.5"
                            >
                                {isSubmitting ? (
                                    <Loader2 className="w-4 h-4 animate-spin" />
                                ) : (
                                    'Invite'
                                )}
                            </button>
                        </div>
                    </form>
                </div>

                {/* Team Roster List */}
                <div>
                    <h3 className="text-xs font-bold text-slate-700 uppercase tracking-wider mb-2">
                        Active Workspace Roster ({users.length})
                    </h3>

                    {generalError && (
                        <div className="dashboard-error-banner text-xs mb-2">
                            <span>{generalError}</span>
                            <button onClick={loadTeam} className="ml-auto underline">Retry</button>
                        </div>
                    )}

                    {isLoading ? (
                        <div className="flex items-center justify-center p-6 text-slate-400 text-xs gap-2">
                            <Loader2 className="w-4 h-4 animate-spin text-indigo-600" />
                            <span>Loading team roster...</span>
                        </div>
                    ) : users.length === 0 ? (
                        <p className="text-xs text-slate-500 py-3 text-center">No team members found.</p>
                    ) : (
                        <div className="border border-slate-200 rounded-lg overflow-hidden max-h-56 overflow-y-auto">
                            <table className="w-full text-left text-xs border-collapse">
                                <thead>
                                    <tr className="bg-slate-50 border-b border-slate-200 text-slate-500 font-semibold">
                                        <th className="p-2.5">MEMBER EMAIL</th>
                                        <th className="p-2.5">ROLE</th>
                                        <th className="p-2.5">STATUS</th>
                                        <th className="p-2.5">DATE ONBOARDED</th>
                                    </tr>
                                </thead>
                                <tbody>
                                    {users.map((u) => {
                                        // Determine role from current workspace membership or fallback
                                        const myMembership = u.memberships?.find((m: any) => m.tenantId === tenantId) || u.memberships?.[0];
                                        const roleName = myMembership ? String(myMembership.role) : 'Staff';
                                        const isOwnerRole = roleName === 'Owner' || roleName === '0';

                                        return (
                                            <tr key={u.id} className="border-b border-slate-100 hover:bg-slate-50/50">
                                                <td className="p-2.5 font-medium text-slate-800">
                                                    {u.email}
                                                </td>
                                                <td className="p-2.5">
                                                    <span className={`px-2 py-0.5 rounded text-xs font-semibold ${
                                                        isOwnerRole
                                                            ? 'bg-purple-100 text-purple-700 border border-purple-200'
                                                            : 'bg-indigo-100 text-indigo-700 border border-indigo-200'
                                                    }`}>
                                                        {isOwnerRole ? 'Owner' : 'Staff'}
                                                    </span>
                                                </td>
                                                <td className="p-2.5">
                                                    <span className="inline-flex items-center gap-1 text-emerald-700">
                                                        <span className="w-1.5 h-1.5 rounded-full bg-emerald-600"></span>
                                                        Active
                                                    </span>
                                                </td>
                                                <td className="p-2.5 text-slate-500">
                                                    <div className="flex items-center gap-1">
                                                        <Calendar className="w-3 h-3 text-slate-400" />
                                                        <span>
                                                            {new Date(u.createdAtUtc).toLocaleDateString(undefined, {
                                                                month: 'short',
                                                                day: 'numeric',
                                                                year: 'numeric',
                                                            })}
                                                        </span>
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
            </div>
        </div>
    );
};
