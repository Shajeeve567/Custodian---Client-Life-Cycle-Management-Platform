export type UserRole = 'Owner' | 'Staff' | 'Client' | 'Admin';

export interface UserProfile {
    userId: string;
    username?: string;
    email: string;
    role: UserRole;
    tenantId?: string;
    tenantName?: string;
}

export interface JwtPayload {
    sub: string;
    email?: string;
    tenant_id?: string;
    'http://schemas.microsoft.com/ws/2008/06/identity/claims/role'?: string;
    role?: string;
    exp?: number;
    iss?: string;
    aud?: string;
}

export interface LoginResponse {
    token: string;
    expiresInMinutes: number;
}

export interface TenantMembership {
    tenantId: string;
    name: string;
    role: string | number;
    createdAtUtc: string;
}

export interface Tenant {
    id: string;
    name: string;
    createdAtUtc: string;
    memberships?: any[];
}

export interface ClientProfile {
    id: string;
    tenantId: string;
    name: string;
    email: string;
    phone?: string;
    status: string | number;
    createdAtUtc: string;
}

export interface CreateClientRequest {
    name: string;
    email: string;
    phone?: string;
}

export interface UserAccountResponse {
    id: string;
    email: string;
    status: string | number;
    createdAtUtc: string;
    memberships?: Array<{ tenantId: string; role: string | number }>;
}

export interface InviteUserRequest {
    email: string;
    password: string;
    role: UserRole;
}

export type EngagementStatus = 'Draft' | 'Started' | 'Closed' | 'Cancelled';

export interface Engagement {
    engagementId: string;
    tenantId: string;
    clientId: string;
    staffId: string;
    status: EngagementStatus;
    createdAt: string;
    closedAt?: string | null;
}

export interface CreateEngagementRequest {
    tenantId: string;
    clientId: string;
    staffId: string;
}

export type ActionType = 'KycDocument' | 'SignAgreement' | 'CustomTask' | 'DocumentUpload';

export interface ClientAction {
    actionId: string;
    engagementId: string;
    tenantId: string;
    title: string;
    description: string;
    type: ActionType | string;
    status?: string;
    stageNumber?: number;
    deadlineUtc?: string | null;
    assignedRole?: UserRole;
    isInternalOnly: boolean;
    isCompleted?: boolean;
    createdAt: string;
    completedAt?: string | null;
    completedByActor?: string | null;
}

export interface CreateClientActionRequest {
    engagementId: string;
    title: string;
    description?: string;
    type: ActionType | string;
    stageNumber?: number;
    deadlineUtc?: string | null;
    assignedRole?: UserRole;
    isInternalOnly?: boolean;
}

export interface ClientSafeAction {
    actionId: string;
    title: string;
    description?: string | null;
    type: string;
    status: string;
    stageNumber: number;
    deadlineUtc?: string | null;
    isOverdue: boolean;
    daysRemaining?: number | null;
}

export interface ClientPortalStage {
    stageNumber: number;
    name: string;
    tagline: string;
    status: 'Completed' | 'Current' | 'Upcoming' | string;
}

export interface ClientPortalDashboard {
    engagementId: string;
    status: EngagementStatus | string;
    createdAt: string;
    currentStageNumber: number;
    currentStageName: string;
    currentStageTagline: string;
    conditionStatus: string;
    conditionDescription: string;
    progressPercentage: number;
    completedTasksCount: number;
    totalTasksCount: number;
    primaryNextAction?: ClientSafeAction | null;
    pendingActions: ClientSafeAction[];
    stages: ClientPortalStage[];
}

export interface UploadActionEvidenceRequest {
    uploaderActor: string;
    documentId?: string;
}

export interface ReviewActionRequest {
    status: 'Completed' | 'Rejected' | string;
    reviewerActor: string;
    reviewNote?: string;
}


export interface AuditEvent {
    eventId: string;
    engagementId: string;
    tenantId: string;
    actor: string;
    type: string;
    timestamp: string;
    payload: string;
    sequenceNumber: number;
    hash: string;
}

export interface DocumentMetadata {
    documentId: string;
    engagementId: string;
    tenantId: string;
    type: string;
    uploaderId: string;
    issueDate: string;
    expiryDate: string;
    uploadedAt: string;
    filePath?: string;
}
