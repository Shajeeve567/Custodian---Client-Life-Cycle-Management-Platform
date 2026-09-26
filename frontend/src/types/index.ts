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
    password: string;
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

export type ActionType =
    | 'UploadDocument'
    | 'ReviewDocument'
    | 'SignDocument'
    | 'CompleteForm'
    | 'VerifyIdentity'
    | 'ScheduleCall'
    | 'ProvideInformation'
    | 'AcknowledgeNotice'
    | 'CustomTask'
    | 'KycDocument'
    | 'SignAgreement'
    | 'ProofOfAddress'
    | 'DocumentUpload';

export type ActionStatus = 'Pending' | 'Uploaded' | 'Completed' | 'Rejected' | 'Cancelled';

export interface ClientAction {
    actionId: string;
    engagementId: string;
    tenantId: string;
    title: string;
    description: string;
    type: ActionType | string;
    status?: ActionStatus | string;
    sourceType?: string;
    stageNumber?: number;
    deadlineUtc?: string | null;
    assignedRole?: UserRole;
    isInternalOnly: boolean;
    isCompleted?: boolean;
    createdAt: string;
    updatedAt?: string;
    activatedAt?: string | null;
    completedAt?: string | null;
    completedByActor?: string | null;
    linkedRequirementId?: string | null;
    linkedDocumentId?: string | null;
    linkedConditionId?: string | null;
    linkedMeetingId?: string | null;
}

export interface CreateClientActionRequest {
    engagementId: string;
    title: string;
    description?: string;
    type: ActionType | string;
    stageNumber?: number;
    deadlineUtc?: string | null;
    // Must match the backend's CreateClientActionDto.AssignedToRole exactly — a mismatched
    // key name here means ASP.NET's model binder silently drops it and defaults to "Client".
    assignedToRole?: UserRole;
    isInternalOnly?: boolean;
    // Backend's CreateClientActionDto.Source is [Required]; omitting it fails ModelState
    // validation (400) before the action is ever persisted.
    source: string;
    sourceType?: string;
    linkedDocumentId?: string;
    linkedConditionId?: string;
    linkedMeetingId?: string;
}

export interface ClientSafeAction {
    actionId: string;
    title: string;
    description?: string | null;
    type: string;
    status: string;
    sourceType?: string;
    stageNumber: number;
    deadlineUtc?: string | null;
    isOverdue: boolean;
    daysRemaining?: number | null;
    rejectionReason?: string | null;
    verificationStatus?: string | null;
    // CSTD-16: set when this action mirrors a Requirement — submit via
    // WorkflowApi.submitRequirement(engagementId, linkedRequirementId, ...) rather than the
    // generic complete/upload flow.
    linkedRequirementId?: string | null;
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
    // CSTD-19: full engine output (client view). The fields above stay for backward compatibility.
    nextAction?: NextActionResult | null;
}

// ---------------------------------------------------------------------------
// CSTD-19: Next-Action Orchestration (GET /api/engagements/{id}/next-action)
// ---------------------------------------------------------------------------

export type NextActionOverallState =
    | 'Closed'
    | 'NotStarted'
    | 'ClientActionRequired'
    | 'AwaitingStaff'
    | 'ReadyToAdvance'
    | 'BlockedExternal'
    | 'AllComplete';

export type NextActionKind =
    | 'RequirementSubmission'
    | 'RequirementReview'
    | 'DocumentUpload'
    | 'DocumentResubmission'
    | 'DocumentVerification'
    | 'ConditionApproval'
    | 'ConditionPayment'
    | 'ClientTask'
    | 'StaffTask'
    | 'AdvanceStage'
    | 'Unavailable';

export interface NextActionItem {
    kind: NextActionKind | string;
    responsibleParty: 'Client' | 'Staff' | string;
    title: string;
    reason: string;
    actionId?: string | null;
    sourceType?: string | null;
    sourceId?: string | null;      // staff view only
    stageNumber?: number | null;
    dueAtUtc?: string | null;
    isOverdue: boolean;
    overdueBy?: string | null;     // staff view only; .NET TimeSpan string, e.g. "2.03:15:00"
    priorityRank: number;          // staff view only
}

export interface GateSummary {
    targetStage: string;
    isSatisfied: boolean;
    reasons: string[];
}

export interface NextActionResult {
    engagementId: string;
    engagementStatus: string;
    currentStage: string;
    overallState: NextActionOverallState | string;
    primaryAction?: NextActionItem | null;
    blockers: NextActionItem[];
    nextStageGate?: GateSummary | null;
    // Informational only (staff view): conditions gating a stage after the next one. Never blockers.
    upcomingConditions: NextActionItem[];
    isStalled: boolean;
    evaluatedAtUtc: string;
}

export interface UploadActionEvidenceRequest {
    uploaderActor: string;
    documentId?: string;
    complianceStatus?: string;
    rejectionReason?: string;
    verificationStatus?: string;
    verificationReason?: string;
    verifiedBy?: string;
}

export interface ReviewActionRequest {
    status: 'Completed' | 'Rejected' | string;
    reviewerActor: string;
    reviewNote?: string;
    verificationStatus?: string;
    verificationReason?: string;
}

export interface ApplyActionVerificationRequest {
    verificationStatus: 'Verified' | 'Rejected' | string;
    verifiedBy: string;
    verificationReason?: string;
}

// CSTD-16 (Requirements Collection)
export interface SubmitRequirementRequest {
    value: string;
    submittedByActor?: string;
}

export interface RequirementResponse {
    requirementId: string;
    engagementId: string;
    tenantId: string;
    type: string;
    status: 'Requested' | 'Submitted' | 'Approved' | 'Rejected' | string;
    stageNumber?: number | null;
    value?: string | null;
    assignedToRole?: string | null;
    requestedBy?: string | null;
    reviewedBy?: string | null;
    requestedAt?: string | null;
    submittedAt?: string | null;
    reviewedAt?: string | null;
    rejectionReason?: string | null;
    createdAt: string;
}

export interface DocumentFilter {
    type?: string;
    complianceStatus?: string;
    verificationStatus?: string;
    uploaderId?: string;
    includeDeleted?: boolean;
}

export interface VerifyDocumentRequest {
    staffNotes?: string;
    staffActor?: string;
}

export interface RejectDocumentRequest {
    reason: string;
    staffActor?: string;
}

export interface UpdateDocumentMetadataRequest {
    type?: string;
    issueDate?: string;
    expiryDate?: string;
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
    issueDate?: string;
    expiryDate?: string;
    fileName?: string;
    contentType?: string;
    fileSize?: number;
    storagePath?: string;
    uploadedAt: string;
    filePath?: string;
    complianceStatus?: string;
    rejectionReason?: string;
    validatedAt?: string;
    verificationStatus?: string;
    verifiedBy?: string | null;
    verifiedAt?: string | null;
    verificationReason?: string | null;
    isDeleted?: boolean;
    deletedAt?: string | null;
    deletedBy?: string | null;
}

export type ConditionType = 'Approval' | 'Payment';
export type ConditionStatus = 'Pending' | 'Satisfied' | 'Rejected';
export type ConditionPaymentType = 'Upfront' | 'Milestone' | 'Final';

export interface EngagementCondition {
    conditionId: string;
    engagementId: string;
    tenantId: string;
    type: ConditionType;
    isActive: boolean;
    status: ConditionStatus;
    requiredBeforeStage: EngagementStage | string;
    title: string;
    description?: string;
    dueDateUtc?: string;
    amount?: number;
    currency?: string;
    paymentType?: string;
    internalNote?: string;
    createdBy: string;
    createdAt: string;
    updatedBy?: string;
    updatedAt?: string;
    deactivatedBy?: string;
    deactivatedAt?: string;
    deactivationReason?: string;
    satisfiedAt?: string;
    satisfiedBy?: string;
    isOverdue?: boolean;
}

export interface ClientSafeCondition {
    conditionId: string;
    title: string;
    description?: string;
    type: ConditionType;
    status: ConditionStatus;
    requiredBeforeStage: string;
    dueDateUtc?: string;
    amount?: number;
    currency?: string;
    paymentType?: string;
    isOverdue: boolean;
}

export interface AttachConditionRequest {
    type: ConditionType;
    requiredBeforeStage?: EngagementStage | string;
    title: string;
    description?: string;
    dueDateUtc?: string;
    amount?: number;
    currency?: string;
    paymentType?: string;
    internalNote?: string;
}

export interface UpdateConditionRequest {
    title?: string;
    description?: string;
    dueDateUtc?: string;
    amount?: number;
    currency?: string;
    paymentType?: string;
    internalNote?: string;
}

export interface DeactivateConditionRequest {
    reason: string;
}

