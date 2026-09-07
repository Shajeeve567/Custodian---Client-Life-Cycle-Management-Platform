import React from 'react';
import { DashboardLayout } from '../components/DashboardLayout';
import { ClientPortalView } from '../components/ClientPortalView';

export const PortalPage: React.FC = () => {
    return (
        <DashboardLayout>
            <div className="w-full max-w-5xl">
                <ClientPortalView />
            </div>
        </DashboardLayout>
    );
};
