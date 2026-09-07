import React from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import { DashboardLayout } from '../components/DashboardLayout';
import { WorkspaceStageView } from '../components/WorkspaceStageView';

export const WorkspacePage: React.FC = () => {
    const { engagementId } = useParams<{ engagementId: string }>();
    const navigate = useNavigate();

    if (!engagementId) {
        return (
            <DashboardLayout>
                <div className="p-8 text-center">
                    <p className="text-slate-600 text-sm">Missing engagement identification.</p>
                    <button
                        onClick={() => navigate('/engagements')}
                        className="mt-4 px-4 py-2 bg-indigo-600 text-white text-xs font-semibold rounded-lg"
                    >
                        Back to Engagements
                    </button>
                </div>
            </DashboardLayout>
        );
    }

    return (
        <DashboardLayout>
            <WorkspaceStageView
                engagementId={engagementId}
                onBack={() => navigate('/engagements')}
            />
        </DashboardLayout>
    );
};
