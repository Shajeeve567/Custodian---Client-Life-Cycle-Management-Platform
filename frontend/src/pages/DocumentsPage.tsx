import React from 'react';
import { DashboardLayout } from '../components/DashboardLayout';
import { DocumentVaultView } from '../components/DocumentVaultView';

export const DocumentsPage: React.FC = () => {
    return (
        <DashboardLayout>
            <div className="w-full max-w-5xl">
                <DocumentVaultView />
            </div>
        </DashboardLayout>
    );
};
