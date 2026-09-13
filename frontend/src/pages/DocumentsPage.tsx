import React from 'react';
import { DashboardLayout } from '../components/DashboardLayout';
import { DocumentVaultView } from '../components/DocumentVaultView';

export const DocumentsPage: React.FC = () => {
    return (
        <DashboardLayout>
            <div className="w-full">
                <DocumentVaultView />
            </div>
        </DashboardLayout>
    );
};
