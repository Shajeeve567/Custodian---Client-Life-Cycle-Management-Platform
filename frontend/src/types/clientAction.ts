export interface ClientActionResponse {
  actionId: string;
  engagementId: string;
  tenantId: string;
  title: string;
  description?: string;
  type: string;
  status: 'Pending' | 'Completed' | 'Cancelled' | 'Overdue';
  source: string;
  isInternalOnly: boolean;
  // Null when returned from the client view (CSTD-12): internal role/actor metadata
  // is stripped server-side and never sent to Client-role callers.
  assignedToRole?: string | null;
  completedByActor?: string | null;
  completedAt?: string;
  createdAt: string;
  sourceMetadata?: string;
  verificationStatus?: string;
  verificationReason?: string;
  stageNumber?: number;
  deadlineUtc?: string | null;
}

export interface CreateClientActionPayload {
  title: string;
  description?: string;
  type: string;
  source: string;
  isInternalOnly?: boolean;
  assignedToRole?: string;
  sourceMetadata?: string;
}

export interface CompleteClientActionPayload {
  completedByActor: string;
}
