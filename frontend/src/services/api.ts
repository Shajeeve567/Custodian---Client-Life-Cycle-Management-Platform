import {
    Engagement,
    EngagementStatus,
    EngagementStage,
    CreateEngagementRequest,
    ClientAction,
    CreateClientActionRequest,
    AuditEvent,
    DocumentMetadata,
    LoginResponse,
    TenantMembership,
    Tenant,
    ClientProfile,
    CreateClientRequest,
    UserAccountResponse,
    InviteUserRequest,
    ClientPortalDashboard,
    ClientSafeAction,
    ClientPortalStage,
    UploadActionEvidenceRequest,
    ReviewActionRequest,
    ApplyActionVerificationRequest,
    DocumentFilter,
    VerifyDocumentRequest,
    RejectDocumentRequest,
    UpdateDocumentMetadataRequest,
    SubmitRequirementRequest,
    RequirementResponse,
    EngagementCondition,
    ClientSafeCondition,
    AttachConditionRequest,
    UpdateConditionRequest,
    DeactivateConditionRequest,
} from '../types';

export const API_BASE = {
    IDENTITY: (import.meta.env.VITE_IDENTITY_URL || import.meta.env.VITE_IDENTITY_API_URL || 'http://localhost:5281').replace(/\/$/, ''),
    WORKFLOW: (import.meta.env.VITE_WORKFLOW_URL || import.meta.env.VITE_WORKFLOW_API_URL || 'http://localhost:5225').replace(/\/$/, ''),
    AUDIT: (import.meta.env.VITE_AUDIT_URL || import.meta.env.VITE_AUDIT_API_URL || 'http://localhost:5051').replace(/\/$/, ''),
    DOCUMENTS: (import.meta.env.VITE_DOCUMENTS_URL || import.meta.env.VITE_DOCUMENTS_API_URL || 'http://localhost:5171').replace(/\/$/, ''),
};

export class ApiError extends Error {
    status: number;
    data: any;

    constructor(status: number, message: string, data?: any) {
        super(message);
        this.name = 'ApiError';
        this.status = status;
        this.data = data;
    }
}

type OnUnauthorizedCallback = () => void;
let unauthorizedHandler: OnUnauthorizedCallback | null = null;

export function setUnauthorizedHandler(handler: OnUnauthorizedCallback | null) {
    unauthorizedHandler = handler;
}

export async function request<T = any>(
    url: string,
    options: RequestInit & { token?: string; skipAuthHeader?: boolean } = {}
): Promise<T> {
    const { token, skipAuthHeader = false, headers: customHeaders, ...rest } = options;

    const headers = new Headers(customHeaders || {});

    // Default Content-Type to application/json unless body is FormData
    if (!(rest.body instanceof FormData) && !headers.has('Content-Type')) {
        headers.set('Content-Type', 'application/json');
    }

    // Determine auth token
    if (!skipAuthHeader) {
        const effectiveToken = token || localStorage.getItem('custodian_token');
        if (effectiveToken) {
            headers.set('Authorization', `Bearer ${effectiveToken}`);
        }
    }

    const response = await fetch(url, {
        ...rest,
        headers,
    });

    // Check status
    if (!response.ok) {
        let errorMessage = response.statusText || 'Request failed';
        let errorData: any = null;

        const contentType = response.headers.get('content-type') || '';
        if (contentType.includes('application/json')) {
            try {
                errorData = await response.json();
                if (errorData?.errors && typeof errorData.errors === 'object') {
                    const messages = Object.values(errorData.errors).flat();
                    errorMessage = messages.join(' ') || errorData.title || errorMessage;
                } else {
                    errorMessage = errorData?.message || errorData?.title || JSON.stringify(errorData);
                }
            } catch {
                errorMessage = await response.text();
            }
        } else {
            errorMessage = await response.text();
        }

        if (response.status === 401 && unauthorizedHandler) {
            unauthorizedHandler();
        }

        throw new ApiError(response.status, errorMessage || `HTTP Error ${response.status}`, errorData);
    }

    // Parse successful response
    const contentType = response.headers.get('content-type') || '';
    if (response.status === 204 || response.headers.get('content-length') === '0') {
        return null as unknown as T;
    }

    if (contentType.includes('application/json')) {
        return (await response.json()) as T;
    }

    // plain text response
    return (await response.text()) as unknown as T;
}

/* ==========================================================================
   Identity Service API
   ========================================================================== */

export const IdentityApi = {
    // 1. Register new user: POST /api/Auth/register -> text/plain 200 or 409
    async register(email: string, password: string): Promise<string> {
        return request<string>(`${API_BASE.IDENTITY}/api/Auth/register`, {
            method: 'POST',
            skipAuthHeader: true,
            body: JSON.stringify({ email, password }),
        });
    },

    // 2. Login: POST /api/Auth/login -> { token, expiresInMinutes } (Global Token)
    async login(email: string, password: string): Promise<LoginResponse> {
        return request<LoginResponse>(`${API_BASE.IDENTITY}/api/Auth/login`, {
            method: 'POST',
            skipAuthHeader: true,
            body: JSON.stringify({ email, password }),
        });
    },

    // 3. Select workspace: POST /api/Auth/select-workspace/{tenantId} -> { token, expiresInMinutes } (Workspace Token)
    async selectWorkspace(tenantId: string, globalToken?: string): Promise<LoginResponse> {
        return request<LoginResponse>(`${API_BASE.IDENTITY}/api/Auth/select-workspace/${tenantId}`, {
            method: 'POST',
            token: globalToken,
        });
    },

    // 4. Create Tenant: POST /api/Tenant (Requires Bearer global token) -> 201 Tenant
    async createTenant(name: string, globalToken?: string): Promise<Tenant> {
        return request<Tenant>(`${API_BASE.IDENTITY}/api/Tenant`, {
            method: 'POST',
            token: globalToken,
            body: JSON.stringify({ name }),
        });
    },

    // 5. Get user's workspaces: GET /api/Tenant/mine (Requires Bearer global token)
    async getMyTenants(globalToken?: string): Promise<TenantMembership[]> {
        return request<TenantMembership[]>(`${API_BASE.IDENTITY}/api/Tenant/mine`, {
            method: 'GET',
            token: globalToken,
        });
    },

    // 6. Get clients: GET /api/Client (Requires Bearer workspace token)
    async getClients(workspaceToken?: string): Promise<ClientProfile[]> {
        return request<ClientProfile[]>(`${API_BASE.IDENTITY}/api/Client`, {
            method: 'GET',
            token: workspaceToken,
        });
    },

    // 7. Create client: POST /api/Client (Requires Bearer workspace token)
    async createClient(data: CreateClientRequest, workspaceToken?: string): Promise<ClientProfile> {
        return request<ClientProfile>(`${API_BASE.IDENTITY}/api/Client`, {
            method: 'POST',
            token: workspaceToken,
            body: JSON.stringify(data),
        });
    },

    // 8. Get team roster: GET /api/UserAccount (Owner only)
    async getUsers(workspaceToken?: string): Promise<UserAccountResponse[]> {
        return request<UserAccountResponse[]>(`${API_BASE.IDENTITY}/api/UserAccount`, {
            method: 'GET',
            token: workspaceToken,
        });
    },

    // 9. Invite staff user: POST /api/UserAccount/invite (Owner only)
    async inviteUser(data: InviteUserRequest, workspaceToken?: string): Promise<UserAccountResponse> {
        const roleMap: Record<string, number> = { 'Owner': 0, 'Staff': 1, 'Client': 2 };
        const roleVal = typeof data.role === 'string' && data.role in roleMap ? roleMap[data.role] : data.role;
        return request<UserAccountResponse>(`${API_BASE.IDENTITY}/api/UserAccount/invite`, {
            method: 'POST',
            token: workspaceToken,
            body: JSON.stringify({
                ...data,
                role: roleVal,
            }),
        });
    },
};

/* ==========================================================================
   Workflow Service API (Authenticated with tenant context)
   ========================================================================== */

export const WorkflowApi = {
    // 1. Create Engagement: POST /api/Engagements { tenantId, clientId, staffId }
    async createEngagement(req: CreateEngagementRequest): Promise<Engagement> {
        return request<Engagement>(`${API_BASE.WORKFLOW}/api/Engagements`, {
            method: 'POST',
            body: JSON.stringify(req),
        });
    },

    // 2. List Engagements: GET /api/Engagements?tenantId=<tenantId>
    async getEngagements(tenantId?: string): Promise<Engagement[]> {
        const url = tenantId
            ? `${API_BASE.WORKFLOW}/api/Engagements?tenantId=${encodeURIComponent(tenantId)}`
            : `${API_BASE.WORKFLOW}/api/Engagements`;
        return request<Engagement[]>(url, {
            method: 'GET',
        });
    },

    async updateStatus(engagementId: string, status: EngagementStatus, tenantId: string): Promise<Engagement> {
        return request<Engagement>(`${API_BASE.WORKFLOW}/api/Engagements/${engagementId}/status`, {
            method: 'PUT',
            body: JSON.stringify({ tenantId, status }),
        });
    },

    // 3. Advance Engagement Stage: PUT /api/Engagements/{id}/stage { tenantId, stage }
    // Only sequential, forward-only transitions succeed server-side (CSTD-17 AC4) —
    // a 400/409 ApiError here reflects a real backend validation rule, not a client bug.
    async updateStage(engagementId: string, stage: EngagementStage, tenantId: string): Promise<Engagement> {
        return request<Engagement>(`${API_BASE.WORKFLOW}/api/Engagements/${engagementId}/stage`, {
            method: 'PUT',
            headers: { 'X-Tenant-ID': tenantId },
            body: JSON.stringify({ tenantId, stage }),
        });
    },

    async deleteEngagement(engagementId: string, tenantId: string): Promise<void> {
        return request<void>(`${API_BASE.WORKFLOW}/api/Engagements/${engagementId}?tenantId=${encodeURIComponent(tenantId)}`, {
            method: 'DELETE',
        });
    },

    async getActions(engagementId: string, tenantId?: string, isClientView?: boolean): Promise<ClientAction[]> {
        const params = new URLSearchParams();
        if (tenantId) params.set('tenantId', tenantId);
        // Backend's view-resolution (CSTD-12) defaults to the client-safe view unless a
        // Staff/Owner caller explicitly asks for isClientView=false — omitting this for a
        // staff-facing caller silently hides every IsInternalOnly task from them.
        if (isClientView !== undefined) params.set('isClientView', String(isClientView));
        const qs = params.toString();
        const url = `${API_BASE.WORKFLOW}/api/Engagements/${engagementId}/actions${qs ? `?${qs}` : ''}`;
        return request<ClientAction[]>(url, {
            method: 'GET',
        });
    },

    async createAction(req: CreateClientActionRequest, tenantId?: string): Promise<ClientAction> {
        const url = tenantId
            ? `${API_BASE.WORKFLOW}/api/Engagements/${req.engagementId}/actions?tenantId=${encodeURIComponent(tenantId)}`
            : `${API_BASE.WORKFLOW}/api/Engagements/${req.engagementId}/actions`;
        return request<ClientAction>(url, {
            method: 'POST',
            body: JSON.stringify(req),
        });
    },

    async completeAction(actionId: string, tenantId: string, engagementId?: string, actor?: string): Promise<ClientAction> {
        const url = engagementId
            ? `${API_BASE.WORKFLOW}/api/engagements/${engagementId}/actions/${actionId}/complete?tenantId=${encodeURIComponent(tenantId)}`
            : `${API_BASE.WORKFLOW}/api/engagements/00000000-0000-0000-0000-000000000000/actions/${actionId}/complete?tenantId=${encodeURIComponent(tenantId)}`;
        return request<ClientAction>(url, {
            method: 'PUT',
            headers: { 'X-Tenant-ID': tenantId },
            body: JSON.stringify({ completedByActor: actor || 'client-user' }),
        });
    },

    async uploadEvidence(
        engagementId: string,
        actionId: string,
        data: UploadActionEvidenceRequest,
        tenantId: string
    ): Promise<ClientAction> {
        return request<ClientAction>(
            `${API_BASE.WORKFLOW}/api/engagements/${engagementId}/actions/${actionId}/upload?tenantId=${encodeURIComponent(tenantId)}`,
            {
                method: 'PUT',
                headers: { 'X-Tenant-ID': tenantId },
                body: JSON.stringify(data),
            }
        );
    },

    async reviewAction(
        engagementId: string,
        actionId: string,
        data: ReviewActionRequest,
        tenantId: string
    ): Promise<ClientAction> {
        return request<ClientAction>(
            `${API_BASE.WORKFLOW}/api/engagements/${engagementId}/actions/${actionId}/review?tenantId=${encodeURIComponent(tenantId)}`,
            {
                method: 'PUT',
                headers: { 'X-Tenant-ID': tenantId },
                body: JSON.stringify(data),
            }
        );
    },

    async applyVerification(
        engagementId: string,
        actionId: string,
        data: ApplyActionVerificationRequest,
        tenantId?: string
    ): Promise<ClientAction> {
        const url = tenantId
            ? `${API_BASE.WORKFLOW}/api/engagements/${engagementId}/actions/${actionId}/verification?tenantId=${encodeURIComponent(tenantId)}`
            : `${API_BASE.WORKFLOW}/api/engagements/${engagementId}/actions/${actionId}/verification`;
        return request<ClientAction>(url, {
            method: 'PUT',
            headers: tenantId ? { 'X-Tenant-ID': tenantId } : undefined,
            body: JSON.stringify(data),
        });
    },

    // CSTD-16 (Requirements Collection)
    async submitRequirement(
        engagementId: string,
        requirementId: string,
        data: SubmitRequirementRequest,
        tenantId: string
    ): Promise<RequirementResponse> {
        return request<RequirementResponse>(
            `${API_BASE.WORKFLOW}/api/engagements/${engagementId}/requirements/${requirementId}/submit?tenantId=${encodeURIComponent(tenantId)}`,
            {
                method: 'PUT',
                headers: { 'X-Tenant-ID': tenantId },
                body: JSON.stringify(data),
            }
        );
    },

    // CSTD-24: Engagement Conditions Management
    async getConditions(engagementId: string, tenantId?: string, includeInactive: boolean = true): Promise<EngagementCondition[]> {
        const params = new URLSearchParams();
        if (tenantId) params.set('tenantId', tenantId);
        params.set('includeInactive', String(includeInactive));
        const qs = params.toString();
        const url = `${API_BASE.WORKFLOW}/api/engagements/${engagementId}/conditions${qs ? `?${qs}` : ''}`;
        return request<EngagementCondition[]>(url, {
            method: 'GET',
            headers: tenantId ? { 'X-Tenant-ID': tenantId } : undefined,
        });
    },

    async getClientConditions(engagementId: string, tenantId?: string): Promise<ClientSafeCondition[]> {
        const params = new URLSearchParams();
        if (tenantId) params.set('tenantId', tenantId);
        const qs = params.toString();
        const url = `${API_BASE.WORKFLOW}/api/engagements/${engagementId}/conditions${qs ? `?${qs}` : ''}`;
        return request<ClientSafeCondition[]>(url, {
            method: 'GET',
            headers: tenantId ? { 'X-Tenant-ID': tenantId } : undefined,
        });
    },

    async attachCondition(engagementId: string, req: AttachConditionRequest, tenantId?: string): Promise<EngagementCondition> {
        const url = tenantId
            ? `${API_BASE.WORKFLOW}/api/engagements/${engagementId}/conditions?tenantId=${encodeURIComponent(tenantId)}`
            : `${API_BASE.WORKFLOW}/api/engagements/${engagementId}/conditions`;
        return request<EngagementCondition>(url, {
            method: 'POST',
            headers: tenantId ? { 'X-Tenant-ID': tenantId } : undefined,
            body: JSON.stringify(req),
        });
    },

    async updateCondition(engagementId: string, conditionId: string, req: UpdateConditionRequest, tenantId?: string): Promise<EngagementCondition> {
        const url = tenantId
            ? `${API_BASE.WORKFLOW}/api/engagements/${engagementId}/conditions/${conditionId}?tenantId=${encodeURIComponent(tenantId)}`
            : `${API_BASE.WORKFLOW}/api/engagements/${engagementId}/conditions/${conditionId}`;
        return request<EngagementCondition>(url, {
            method: 'PATCH',
            headers: tenantId ? { 'X-Tenant-ID': tenantId } : undefined,
            body: JSON.stringify(req),
        });
    },

    async deactivateCondition(engagementId: string, conditionId: string, reason: string, tenantId?: string): Promise<EngagementCondition> {
        const url = tenantId
            ? `${API_BASE.WORKFLOW}/api/engagements/${engagementId}/conditions/${conditionId}/deactivate?tenantId=${encodeURIComponent(tenantId)}`
            : `${API_BASE.WORKFLOW}/api/engagements/${engagementId}/conditions/${conditionId}/deactivate`;
        return request<EngagementCondition>(url, {
            method: 'PUT',
            headers: tenantId ? { 'X-Tenant-ID': tenantId } : undefined,
            body: JSON.stringify({ reason }),
        });
    },
};

/* ==========================================================================
   Client Portal BFF API (Client-Safe Aggregation)
   ========================================================================== */

export const PortalApi = {
    // 1. Automatically fetch the authenticated client's active engagement dashboard
    async getMyEngagement(tenantId?: string | null, clientId?: string | null): Promise<ClientPortalDashboard> {
        let url = `${API_BASE.WORKFLOW}/api/portal/my-engagement`;
        const params = new URLSearchParams();
        if (tenantId) params.append('tenantId', tenantId);
        if (clientId) params.append('clientId', clientId);
        const query = params.toString();
        if (query) url += `?${query}`;

        const headers: Record<string, string> = {};
        if (tenantId) headers['X-Tenant-ID'] = tenantId;
        if (clientId) headers['X-Client-ID'] = clientId;

        return request<ClientPortalDashboard>(url, {
            method: 'GET',
            headers,
        });
    },

    // 2. Fetch client-safe dashboard for a specific engagement GUID (enforcing IDOR isolation)
    async getEngagementDashboard(engagementId: string, tenantId?: string | null, clientId?: string | null): Promise<ClientPortalDashboard> {
        let url = `${API_BASE.WORKFLOW}/api/portal/engagements/${engagementId}`;
        const params = new URLSearchParams();
        if (tenantId) params.append('tenantId', tenantId);
        if (clientId) params.append('clientId', clientId);
        const query = params.toString();
        if (query) url += `?${query}`;

        const headers: Record<string, string> = {};
        if (tenantId) headers['X-Tenant-ID'] = tenantId;
        if (clientId) headers['X-Client-ID'] = clientId;

        return request<ClientPortalDashboard>(url, {
            method: 'GET',
            headers,
        });
    },
};

/* ==========================================================================
   Audit & Documents Service APIs (Preserved for existing views)
   ========================================================================== */

export const AuditApi = {
    async getEvents(tenantId?: string, engagementId?: string): Promise<AuditEvent[]> {
        let url = `${API_BASE.AUDIT}/events`;
        const params = new URLSearchParams();
        if (tenantId) params.append('tenantId', tenantId);
        if (engagementId) params.append('engagementId', engagementId);
        const query = params.toString();
        if (query) url += `?${query}`;
        return request<AuditEvent[]>(url);
    },

    async verifyChain(tenantId?: string): Promise<{ isVerified: boolean; count: number }> {
        const url = tenantId
            ? `${API_BASE.AUDIT}/events/verify?tenantId=${encodeURIComponent(tenantId)}`
            : `${API_BASE.AUDIT}/events/verify`;
        return request<{ isVerified: boolean; count: number }>(url);
    },
};

export const DocumentsApi = {
    async getDocuments(
        engagementId: string,
        filterOrTenantId?: DocumentFilter | string,
        optionalTenantId?: string
    ): Promise<DocumentMetadata[]> {
        const filter = typeof filterOrTenantId === 'object' ? filterOrTenantId : undefined;
        const tenantId = typeof filterOrTenantId === 'string' ? filterOrTenantId : optionalTenantId;

        const params = new URLSearchParams();
        if (tenantId) params.append('tenantId', tenantId);
        if (filter?.type) params.append('type', filter.type);
        if (filter?.complianceStatus) params.append('complianceStatus', filter.complianceStatus);
        if (filter?.verificationStatus) params.append('verificationStatus', filter.verificationStatus);
        if (filter?.uploaderId) params.append('uploaderId', filter.uploaderId);
        if (filter?.includeDeleted !== undefined) params.append('includeDeleted', String(filter.includeDeleted));

        const query = params.toString();
        const url = `${API_BASE.DOCUMENTS}/api/engagements/${engagementId}/documents${query ? `?${query}` : ''}`;
        return request<DocumentMetadata[]>(url, {
            headers: tenantId ? { 'X-Tenant-ID': tenantId } : undefined,
        });
    },

    async uploadDocument(engagementId: string, formData: FormData, tenantId?: string): Promise<DocumentMetadata> {
        const url = tenantId
            ? `${API_BASE.DOCUMENTS}/api/engagements/${engagementId}/documents?tenantId=${encodeURIComponent(tenantId)}`
            : `${API_BASE.DOCUMENTS}/api/engagements/${engagementId}/documents`;
        return request<DocumentMetadata>(url, {
            method: 'POST',
            headers: tenantId ? { 'X-Tenant-ID': tenantId } : undefined,
            body: formData,
        });
    },

    async verifyDocument(
        engagementId: string,
        documentId: string,
        req: VerifyDocumentRequest,
        tenantId?: string
    ): Promise<DocumentMetadata> {
        const url = tenantId
            ? `${API_BASE.DOCUMENTS}/api/engagements/${engagementId}/documents/${documentId}/verify?tenantId=${encodeURIComponent(tenantId)}`
            : `${API_BASE.DOCUMENTS}/api/engagements/${engagementId}/documents/${documentId}/verify`;
        return request<DocumentMetadata>(url, {
            method: 'POST',
            headers: tenantId ? { 'X-Tenant-ID': tenantId } : undefined,
            body: JSON.stringify(req),
        });
    },

    async rejectDocument(
        engagementId: string,
        documentId: string,
        req: RejectDocumentRequest,
        tenantId?: string
    ): Promise<DocumentMetadata> {
        const url = tenantId
            ? `${API_BASE.DOCUMENTS}/api/engagements/${engagementId}/documents/${documentId}/reject?tenantId=${encodeURIComponent(tenantId)}`
            : `${API_BASE.DOCUMENTS}/api/engagements/${engagementId}/documents/${documentId}/reject`;
        return request<DocumentMetadata>(url, {
            method: 'POST',
            headers: tenantId ? { 'X-Tenant-ID': tenantId } : undefined,
            body: JSON.stringify(req),
        });
    },

    async updateDocumentMetadata(
        engagementId: string,
        documentId: string,
        req: UpdateDocumentMetadataRequest,
        tenantId?: string
    ): Promise<DocumentMetadata> {
        const url = tenantId
            ? `${API_BASE.DOCUMENTS}/api/engagements/${engagementId}/documents/${documentId}/metadata?tenantId=${encodeURIComponent(tenantId)}`
            : `${API_BASE.DOCUMENTS}/api/engagements/${engagementId}/documents/${documentId}/metadata`;
        return request<DocumentMetadata>(url, {
            method: 'PUT',
            headers: tenantId ? { 'X-Tenant-ID': tenantId } : undefined,
            body: JSON.stringify(req),
        });
    },

    async deleteDocument(engagementId: string, documentId: string, tenantId?: string): Promise<DocumentMetadata> {
        const url = tenantId
            ? `${API_BASE.DOCUMENTS}/api/engagements/${engagementId}/documents/${documentId}?tenantId=${encodeURIComponent(tenantId)}`
            : `${API_BASE.DOCUMENTS}/api/engagements/${engagementId}/documents/${documentId}`;
        return request<DocumentMetadata>(url, {
            method: 'DELETE',
            headers: tenantId ? { 'X-Tenant-ID': tenantId } : undefined,
        });
    },

    getDownloadUrl(engagementId: string, documentId: string, tenantId?: string, includeDeleted: boolean = false): string {
        const params = new URLSearchParams();
        if (tenantId) params.append('tenantId', tenantId);
        if (includeDeleted) params.append('includeDeleted', 'true');
        const query = params.toString();
        const base = `${API_BASE.DOCUMENTS}/api/engagements/${engagementId}/documents/${documentId}/download`;
        return query ? `${base}?${query}` : base;
    },
};

