import {
    Engagement,
    EngagementStatus,
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
   Workflow Service API (No auth middleware - tenant passed in body/query)
   ========================================================================== */

export const WorkflowApi = {
    // 1. Create Engagement: POST /api/Engagements { tenantId, clientId, staffId }
    async createEngagement(req: CreateEngagementRequest): Promise<Engagement> {
        return request<Engagement>(`${API_BASE.WORKFLOW}/api/Engagements`, {
            method: 'POST',
            skipAuthHeader: true,
            body: JSON.stringify(req),
        });
    },

    // 2. List Engagements: GET /api/Engagements?tenantId=<tenantId>
    async getEngagements(tenantId: string): Promise<Engagement[]> {
        return request<Engagement[]>(`${API_BASE.WORKFLOW}/api/Engagements?tenantId=${encodeURIComponent(tenantId)}`, {
            method: 'GET',
            skipAuthHeader: true,
        });
    },

    async updateStatus(engagementId: string, status: EngagementStatus, tenantId: string): Promise<Engagement> {
        return request<Engagement>(`${API_BASE.WORKFLOW}/api/Engagements/${engagementId}/status`, {
            method: 'PUT',
            skipAuthHeader: true,
            body: JSON.stringify({ tenantId, status }),
        });
    },

    async deleteEngagement(engagementId: string, tenantId: string): Promise<void> {
        return request<void>(`${API_BASE.WORKFLOW}/api/Engagements/${engagementId}`, {
            method: 'DELETE',
            skipAuthHeader: true,
        });
    },

    async getActions(engagementId: string, tenantId: string): Promise<ClientAction[]> {
        return request<ClientAction[]>(`${API_BASE.WORKFLOW}/api/Engagements/${engagementId}/actions`, {
            method: 'GET',
            skipAuthHeader: true,
        });
    },

    async createAction(req: CreateClientActionRequest, tenantId: string): Promise<ClientAction> {
        return request<ClientAction>(`${API_BASE.WORKFLOW}/api/Engagements/${req.engagementId}/actions`, {
            method: 'POST',
            skipAuthHeader: true,
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
    async getEvents(tenantId: string, engagementId?: string): Promise<AuditEvent[]> {
        let url = `${API_BASE.AUDIT}/events?tenantId=${tenantId}`;
        if (engagementId) url += `&engagementId=${engagementId}`;
        return request<AuditEvent[]>(url);
    },

    async verifyChain(tenantId: string): Promise<{ isVerified: boolean; count: number }> {
        return request<{ isVerified: boolean; count: number }>(`${API_BASE.AUDIT}/events/verify?tenantId=${tenantId}`);
    },
};

export const DocumentsApi = {
    async getDocuments(engagementId: string, tenantId: string): Promise<DocumentMetadata[]> {
        return request<DocumentMetadata[]>(`${API_BASE.DOCUMENTS}/engagements/${engagementId}/documents`);
    },

    async uploadDocument(engagementId: string, formData: FormData, tenantId: string): Promise<DocumentMetadata> {
        return request<DocumentMetadata>(`${API_BASE.DOCUMENTS}/engagements/${engagementId}/documents`, {
            method: 'POST',
            body: formData,
        });
    },

    getDownloadUrl(engagementId: string, documentId: string): string {
        return `${API_BASE.DOCUMENTS}/engagements/${engagementId}/documents/${documentId}/download`;
    },
};
