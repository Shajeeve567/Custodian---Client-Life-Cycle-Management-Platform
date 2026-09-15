import React from 'react';
import { DashboardLayout } from '../components/DashboardLayout';
import { ReportsView } from '../components/ReportsView';

export const ReportsPage: React.FC = () => {
    return (
        <DashboardLayout>
            <div className="w-full max-w-5xl">
                <ReportsView />
            </div>
        </DashboardLayout>
    );
};
