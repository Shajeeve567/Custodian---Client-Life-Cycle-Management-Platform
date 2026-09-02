import React, { createContext, useContext, useState } from 'react';
import { UserRole } from '../types';

interface AuthContextType {
    tenantId: string;
    setTenantId: (tenant: string) => void;
    role: UserRole;
    setRole: (role: UserRole) => void;
    userId: string;
    setUserId: (id: string) => void;
}

const AuthContext = createContext<AuthContextType | undefined>(undefined);

export const AuthProvider: React.FC<{ children: React.ReactNode }> = ({ children }) => {
    const [tenantId, setTenantId] = useState<string>('tenant-alpha');
    const [role, setRole] = useState<UserRole>('Staff');
    const [userId, setUserId] = useState<string>('usr-101');

    return (
        <AuthContext.Provider value={{ tenantId, setTenantId, role, setRole, userId, setUserId }}>
            {children}
        </AuthContext.Provider>
    );
};

export const useAuth = () => {
    const context = useContext(AuthContext);
    if (!context) throw new Error('useAuth must be used within AuthProvider');
    return context;
};
