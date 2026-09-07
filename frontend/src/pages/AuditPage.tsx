import React from 'react';
import { DashboardLayout } from '../components/DashboardLayout';
import { AuditLogView } from '../components/AuditLogView';

export const AuditPage: React.FC = () => {
    return (
        <DashboardLayout>
            <div className="w-full max-w-5xl">
                <AuditLogView />
            </div>
        </DashboardLayout>
    );
};
