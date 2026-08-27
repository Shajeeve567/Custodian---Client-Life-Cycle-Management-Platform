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
  assignedToRole: string;
  completedByActor?: string;
  completedAt?: string;
  createdAt: string;
  sourceMetadata?: string;
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
