import React, { createContext, useContext, useState, useEffect, useCallback } from 'react';
import { jwtDecode } from 'jwt-decode';
import { JwtPayload, UserRole } from '../types';
import { setUnauthorizedHandler } from '../services/api';

interface AuthContextType {
    token: string | null;
    tenantId: string | null;
    tenantName: string | null;
    userId: string | null;
    email: string | null;
    role: UserRole;
    isAuthenticated: boolean;
    isLoading: boolean;
    setWorkspaceToken: (token: string, tenantId: string, tenantName?: string) => void;
    logout: () => void;
}

const AuthContext = createContext<AuthContextType | undefined>(undefined);

const TOKEN_KEY = 'custodian_token';
const TENANT_KEY = 'custodian_tenant_id';
const TENANT_NAME_KEY = 'custodian_tenant_name';

function parseRole(roleClaim?: string): UserRole {
    if (!roleClaim) return 'Staff';
    if (roleClaim === 'Owner') return 'Owner';
    if (roleClaim === 'Staff') return 'Staff';
    if (roleClaim === 'Client') return 'Client';
    if (roleClaim === 'Admin') return 'Admin';
    return 'Staff';
}

export const AuthProvider: React.FC<{ children: React.ReactNode }> = ({ children }) => {
    const [token, setToken] = useState<string | null>(null);
    const [tenantId, setTenantId] = useState<string | null>(null);
    const [tenantName, setTenantName] = useState<string | null>(null);
    const [userId, setUserId] = useState<string | null>(null);
    const [email, setEmail] = useState<string | null>(null);
    const [role, setRole] = useState<UserRole>('Staff');
    const [isLoading, setIsLoading] = useState<boolean>(true);

    const logout = useCallback(() => {
        localStorage.removeItem(TOKEN_KEY);
        localStorage.removeItem(TENANT_KEY);
        localStorage.removeItem(TENANT_NAME_KEY);
        setToken(null);
        setTenantId(null);
        setTenantName(null);
        setUserId(null);
        setEmail(null);
        setRole('Staff');
    }, []);

    const setWorkspaceToken = useCallback((newToken: string, newTenantId: string, newTenantName?: string) => {
        try {
            const decoded = jwtDecode<JwtPayload>(newToken);
            const sub = decoded.sub;
            const userEmail = decoded.email || null;
            const roleClaim = decoded['http://schemas.microsoft.com/ws/2008/06/identity/claims/role'] || decoded.role;
            const parsedRole = parseRole(roleClaim);
            const tid = decoded.tenant_id || newTenantId;

            localStorage.setItem(TOKEN_KEY, newToken);
            localStorage.setItem(TENANT_KEY, tid);
            if (newTenantName) {
                localStorage.setItem(TENANT_NAME_KEY, newTenantName);
                setTenantName(newTenantName);
            }

            setToken(newToken);
            setTenantId(tid);
            setUserId(sub);
            setEmail(userEmail);
            setRole(parsedRole);
        } catch (err) {
            console.error('Failed to decode workspace token:', err);
            logout();
        }
    }, [logout]);

    useEffect(() => {
        setUnauthorizedHandler(logout);
        return () => {
            setUnauthorizedHandler(null);
        };
    }, [logout]);

    useEffect(() => {
        try {
            const savedToken = localStorage.getItem(TOKEN_KEY);
            const savedTenantId = localStorage.getItem(TENANT_KEY);
            const savedTenantName = localStorage.getItem(TENANT_NAME_KEY);

            if (savedToken) {
                const decoded = jwtDecode<JwtPayload>(savedToken);

                // Check expiration
                if (decoded.exp && decoded.exp * 1000 < Date.now()) {
                    logout();
                } else if (decoded.tenant_id || savedTenantId) {
                    const tid = decoded.tenant_id || savedTenantId || null;
                    const sub = decoded.sub || null;
                    const userEmail = decoded.email || null;
                    const roleClaim = decoded['http://schemas.microsoft.com/ws/2008/06/identity/claims/role'] || decoded.role;

                    setToken(savedToken);
                    setTenantId(tid);
                    setTenantName(savedTenantName || null);
                    setUserId(sub);
                    setEmail(userEmail);
                    setRole(parseRole(roleClaim));
                } else {
                    // Token exists but has no tenant_id claim - cannot be used as workspace token
                    logout();
                }
            }
        } catch (err) {
            console.error('Error restoring auth session:', err);
            logout();
        } finally {
            setIsLoading(false);
        }
    }, [logout]);

    const isAuthenticated = Boolean(token && tenantId && userId);

    return (
        <AuthContext.Provider
            value={{
                token,
                tenantId,
                tenantName,
                userId,
                email,
                role,
                isAuthenticated,
                isLoading,
                setWorkspaceToken,
                logout,
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
