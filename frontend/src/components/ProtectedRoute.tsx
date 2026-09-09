import React from 'react';
import { Navigate, Outlet } from 'react-router-dom';
import { useAuth } from '../context/AuthContext';
import { UserRole } from '../types';

interface ProtectedRouteProps {
    children?: React.ReactNode;
    allowedRoles?: UserRole[];
}

export const ProtectedRoute: React.FC<ProtectedRouteProps> = ({ children, allowedRoles }) => {
    const { isAuthenticated, isLoading, role } = useAuth();

    if (isLoading) {
        return (
            <div className="flex-center min-h-screen">
                <div className="loading-spinner"></div>
            </div>
        );
    }

    if (!isAuthenticated) {
        return <Navigate to="/login" replace />;
    }

    // Role-based authorization guard
    if (allowedRoles && !allowedRoles.includes(role)) {
        // Enforce strict client isolation: Client accounts are restricted to the client portal
        if (role === 'Client') {
            return <Navigate to="/portal" replace />;
        }
        // Non-clients trying to access restricted pages default to engagements
        return <Navigate to="/engagements" replace />;
    }

    return children ? <>{children}</> : <Outlet />;
};

