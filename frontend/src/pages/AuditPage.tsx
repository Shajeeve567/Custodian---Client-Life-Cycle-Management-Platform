import React from 'react';
import { DashboardLayout } from '../components/DashboardLayout';
import { AuditLogView } from '../components/AuditLogView';

export const AuditPage: React.FC = () => {
    return (
        <DashboardLayout>
            <AuditLogView />
        </DashboardLayout>
    );
};
