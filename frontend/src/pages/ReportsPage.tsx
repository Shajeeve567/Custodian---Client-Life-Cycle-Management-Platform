import React from 'react';
import { DashboardLayout } from '../components/DashboardLayout';
import { ReportsView } from '../components/reports/ReportsView';

export const ReportsPage: React.FC = () => {
    return (
        <DashboardLayout>
            <ReportsView />
        </DashboardLayout>
    );
};
