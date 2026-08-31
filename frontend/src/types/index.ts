export type UserRole = 'Staff' | 'Client' | 'Admin';

export interface UserProfile {
    userId: string;
    username: string;
    email: string;
    role: UserRole;
    tenantId: string;
}

export type EngagementStatus = 'Draft' | 'InProgress' | 'Closed';

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
