export type ClientActionStatusType = 'Pending' | 'Uploaded' | 'Completed' | 'Rejected' | 'Cancelled';

export interface ClientActionResponse {
  actionId: string;
  engagementId: string;
  tenantId: string;
  title: string;
  description?: string;
  type: string;
  status: ClientActionStatusType;
  source: string;
  sourceType?: string;
  isInternalOnly: boolean;
  // Null when returned from the client view (CSTD-12): internal role/actor metadata
  // is stripped server-side and never sent to Client-role callers.
  assignedToRole?: string | null;
  completedByActor?: string | null;
  completedAt?: string | null;
  createdAt: string;
  updatedAt?: string;
  activatedAt?: string | null;
  sourceMetadata?: string | null;
  verificationStatus?: string | null;
  verificationReason?: string | null;
  stageNumber?: number;
  deadlineUtc?: string | null;
  linkedRequirementId?: string | null;
  linkedDocumentId?: string | null;
  linkedConditionId?: string | null;
  linkedMeetingId?: string | null;
}

export interface CreateClientActionPayload {
  title: string;
  description?: string;
  type: string;
  source: string;
  sourceType?: string;
  isInternalOnly?: boolean;
  assignedToRole?: string;
  sourceMetadata?: string;
  stageNumber?: number;
  deadlineUtc?: string;
  linkedDocumentId?: string;
  linkedConditionId?: string;
  linkedMeetingId?: string;
}

export interface CompleteClientActionPayload {
  completedByActor: string;
}

