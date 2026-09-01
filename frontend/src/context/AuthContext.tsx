import React, { createContext, useContext, useState, useEffect } from 'react';
import { UserRole, LoginRequest, CreateUserRequest } from '../types';
import { IdentityApi } from '../services/api';

interface AuthContextType {
    token: string | null;
    tenantId: string;
    setTenantId: (tenant: string) => void;
    role: UserRole;
    setRole: (role: UserRole) => void;
    userId: string;
    setUserId: (id: string) => void;
    userEmail: string;
    isAuthenticated: boolean;
    login: (req: LoginRequest) => Promise<void>;
    selectWorkspace: (tenantId: string) => Promise<void>;
    register: (req: CreateUserRequest) => Promise<void>;
    logout: () => void;
}

const AuthContext = createContext<AuthContextType | undefined>(undefined);

function parseJwt(token: string) {
    try {
        const base64Url = token.split('.')[1];
        const base64 = base64Url.replace(/-/g, '+').replace(/_/g, '/');
        const jsonPayload = decodeURIComponent(
            atob(base64)
                .split('')
                .map((c) => '%' + ('00' + c.charCodeAt(0).toString(16)).slice(-2))
                .join('')
        );
        return JSON.parse(jsonPayload);
    } catch {
        return null;
    }
}

export const AuthProvider: React.FC<{ children: React.ReactNode }> = ({ children }) => {
    const [token, setToken] = useState<string | null>(() => localStorage.getItem('custodian_jwt'));
    const [tenantId, setTenantId] = useState<string>(() => localStorage.getItem('custodian_tenant') || 'tenant-alpha');
    const [role, setRole] = useState<UserRole>(() => (localStorage.getItem('custodian_role') as UserRole) || 'Staff');
    const [userId, setUserId] = useState<string>(() => localStorage.getItem('custodian_user_id') || 'usr-101');
    const [userEmail, setUserEmail] = useState<string>(() => localStorage.getItem('custodian_user_email') || 'staff@custodian.com');

    const applyTokenState = (newToken: string) => {
        const payload = parseJwt(newToken);
        if (payload) {
            if (payload.sub) {
                setUserId(payload.sub);
                localStorage.setItem('custodian_user_id', payload.sub);
            }
            if (payload.email) {
                setUserEmail(payload.email);
                localStorage.setItem('custodian_user_email', payload.email);
            }
            if (payload.tenant_id) {
                setTenantId(payload.tenant_id);
                localStorage.setItem('custodian_tenant', payload.tenant_id);
            }
            const rawRole = payload['http://schemas.microsoft.com/ws/2008/06/identity/claims/role'] || payload.role;
            if (rawRole) {
                let parsedRole: UserRole = 'Staff';
                const lower = rawRole.toLowerCase();
                if (lower === 'client') parsedRole = 'Client';
                else if (lower === 'owner' || lower === 'admin') parsedRole = 'Admin';
                else parsedRole = 'Staff';

                setRole(parsedRole);
                localStorage.setItem('custodian_role', parsedRole);
            }
        }
    };

    useEffect(() => {
        if (token) {
            localStorage.setItem('custodian_jwt', token);
            applyTokenState(token);
        } else {
            localStorage.removeItem('custodian_jwt');
        }
    }, [token]);

    useEffect(() => {
        localStorage.setItem('custodian_tenant', tenantId);
        localStorage.setItem('custodian_role', role);
        localStorage.setItem('custodian_user_id', userId);
        localStorage.setItem('custodian_user_email', userEmail);
    }, [tenantId, role, userId, userEmail]);

    const login = async (req: LoginRequest) => {
        const res = await IdentityApi.login(req);
        setToken(res.token);
        setUserEmail(req.email);
        applyTokenState(res.token);

        try {
            const wsRes = await IdentityApi.selectWorkspace(tenantId, res.token);
            setToken(wsRes.token);
            applyTokenState(wsRes.token);
        } catch (err) {
            console.warn('Auto workspace selection failed on login:', err);
        }
    };

    const selectWorkspace = async (targetTenantId: string) => {
        if (token) {
            try {
                const res = await IdentityApi.selectWorkspace(targetTenantId, token);
                setToken(res.token);
                applyTokenState(res.token);
            } catch (err) {
                console.warn('Workspace switch token update failed, falling back to local tenant update:', err);
            }
        }
        setTenantId(targetTenantId);
    };


    const register = async (req: CreateUserRequest) => {
        await IdentityApi.register(req, tenantId, token || undefined);
    };

    const logout = () => {
        setToken(null);
        localStorage.removeItem('custodian_jwt');
    };

    return (
        <AuthContext.Provider
            value={{
                token,
                tenantId,
                setTenantId,
                role,
                setRole,
                userId,
                setUserId,
                userEmail,
                isAuthenticated: !!token,
                login,
                selectWorkspace,
                register,
                logout
            }}
        >
            {children}
        </AuthContext.Provider>
    );
};

export const useAuth = () => {
    const context = useContext(AuthContext);
    if (!context) throw new Error('useAuth must be used within AuthProvider');
    return context;
};

