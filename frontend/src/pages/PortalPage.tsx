import React from 'react';
import { useSearchParams } from 'react-router-dom';
import { DashboardLayout } from '../components/DashboardLayout';
import { ClientPortalView } from '../components/ClientPortalView';

export const PortalPage: React.FC = () => {
    const [searchParams] = useSearchParams();
    const engagementId = searchParams.get('engagementId') || undefined;

    return (
        <DashboardLayout>
            <div className="w-full max-w-5xl mx-auto">
                <ClientPortalView engagementId={engagementId} />
            </div>
        </DashboardLayout>
    );
};

