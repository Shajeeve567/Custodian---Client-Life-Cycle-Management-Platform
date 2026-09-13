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

// The 5 canonical engagement pipeline stages (CSTD-17). Must match the backend's
// EngagementStage enum exactly (Workflow service, Models/EngagementStage.cs).
export type EngagementStage = 'Onboarding' | 'DocumentCollection' | 'Verification' | 'Execution' | 'Closure';

export interface Engagement {
    engagementId: string;
    tenantId: string;
    clientId: string;
    staffId: string;
    status: EngagementStatus;
    stage: EngagementStage;
    stageProgressPercentage: number;
    createdAt: string;
    closedAt?: string | null;
}

export interface CreateEngagementRequest {
    tenantId: string;
    clientId: string;
    staffId: string;
}

export interface UpdateEngagementStageRequest {
    tenantId: string;
    stage: EngagementStage;
}

export type ActionType = 'KycDocument' | 'SignAgreement' | 'CustomTask';

export interface ClientAction {
    actionId: string;
    engagementId: string;
    tenantId: string;
    title: string;
    description: string;
    type: ActionType;
    assignedRole: UserRole;
    isInternalOnly: boolean;
    isCompleted: boolean;
    createdAt: string;
    completedAt?: string | null;
}

export interface CreateClientActionRequest {
    engagementId: string;
    title: string;
    description: string;
    type: ActionType;
    assignedRole: UserRole;
    isInternalOnly: boolean;
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
