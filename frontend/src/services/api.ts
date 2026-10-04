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
    RequestRequirementRequest,
    ReviewRequirementRequest,
    RequirementResponse,
    ChainVerificationResult,
    StallQueueFilters,
    StallQueueResult,
    EngagementCondition,
    ClientSafeCondition,
    AttachConditionRequest,
    UpdateConditionRequest,
    DeactivateConditionRequest,
    NextActionResult,
    UpdateClientActionRequest,
    StallQueueItem,
    Intervention,
    RecordInterventionRequest,
    Meeting,
    CreateMeetingRequest,
    UpdateMeetingRequest,
    RescheduleMeetingRequest,
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

/**
 * fetch with the caller's bearer token and the shared error handling (ApiError, 401 handler).
 * Returns the successful Response unread, so callers can take JSON, text or a Blob.
 */
async function authorizedFetch(
    url: string,
    options: RequestInit & { token?: string; skipAuthHeader?: boolean } = {}
): Promise<Response> {
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
        // ASP.NET error bodies (ProblemDetails) are application/problem+json.
        if (contentType.includes('application/json') || contentType.includes('+json')) {
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

    return response;
}

export async function request<T = any>(
    url: string,
    options: RequestInit & { token?: string; skipAuthHeader?: boolean } = {}
): Promise<T> {
    const response = await authorizedFetch(url, options);

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
   Reports (CSTD-36)
   ========================================================================== */

/** Report endpoints (each hosted by the service that owns the data). */
export const ReportsApi = {
    slaPerformanceUrl: () => `${API_BASE.WORKFLOW}/api/reports/sla-performance`,
};

/** RFC 7807 body of a failed report request (see docs/reporting.md, Errors). */
export interface ReportProblem {
    status: number;
    title: string;
    detail?: string;
    reportCode?: string;
    correlationId?: string;
    /** The filter at fault, for "Invalid report filter". */
    field?: string;
}

export class ReportDownloadError extends Error {
    problem: ReportProblem;

    constructor(problem: ReportProblem) {
        super(problem.detail ? `${problem.title}: ${problem.detail}` : problem.title);
        this.name = 'ReportDownloadError';
        this.problem = problem;
    }
}

export type ReportParams = Record<string, string | number | boolean | null | undefined>;

/** File name from `Content-Disposition` (quoted, bare or RFC 5987 `filename*=`), else `fallback`. */
export function reportFileName(contentDisposition: string | null, fallback: string): string {
    if (!contentDisposition) return fallback;
    const extended = /filename\*\s*=\s*(?:UTF-8|utf-8)''([^;]+)/.exec(contentDisposition);
    if (extended) {
        try {
            return decodeURIComponent(extended[1].trim());
        } catch {
            /* fall through to the plain filename */
        }
    }
    const plain = /filename\s*=\s*("([^"]*)"|[^;]+)/.exec(contentDisposition);
    const name = (plain?.[2] ?? plain?.[1] ?? '').trim();
    return name || fallback;
}

function toReportProblem(err: unknown): ReportProblem {
    if (err instanceof ApiError) {
        const data = err.data && typeof err.data === 'object' ? err.data : {};
        return {
            status: err.status,
            title: data.title || (err.status === 403 ? 'You do not have access to this report' : 'Report could not be generated'),
            detail: data.detail || (data.title ? undefined : err.message || undefined),
            reportCode: data.reportCode,
            correlationId: data.correlationId,
            field: data.field,
        };
    }
    // Network failure (service down, CORS): fetch rejects without a response.
    return {
        status: 0,
        title: 'Report data source unavailable — try again',
        detail: err instanceof Error ? err.message : undefined,
    };
}

/**
 * Downloads a report (CSTD-36 endpoint convention: GET api/reports/{slug}?format=pdf|csv&filters) with the
 * caller's token, saving it under the server's file name. Empty/undefined params are left out.
 * Throws ReportDownloadError carrying the ProblemDetails (title, detail, field, correlationId).
 */
export async function downloadReport(url: string, params: ReportParams = {}): Promise<{ fileName: string }> {
    const query = new URLSearchParams();
    Object.entries(params).forEach(([key, value]) => {
        if (value !== undefined && value !== null && value !== '') query.append(key, String(value));
    });
    const fullUrl = query.toString() ? `${url}${url.includes('?') ? '&' : '?'}${query.toString()}` : url;

    let response: Response;
    try {
        response = await authorizedFetch(fullUrl, { headers: { Accept: 'application/pdf, text/csv, application/problem+json' } });
    } catch (err) {
        throw new ReportDownloadError(toReportProblem(err));
    }

    const fallback = `custodian-report.${String(params.format || 'pdf').toLowerCase()}`;
    const fileName = reportFileName(response.headers.get('content-disposition'), fallback);
    const blob = await response.blob();
    const objectUrl = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = objectUrl;
    link.download = fileName;
    document.body.appendChild(link);
    link.click();
    link.remove();
    // Give the browser a moment to start the download before releasing the blob.
    setTimeout(() => URL.revokeObjectURL(objectUrl), 10_000);
    return { fileName };
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

    async getStallQueue(tenantId: string, filters: StallQueueFilters = {}): Promise<StallQueueResult> {
        const params = new URLSearchParams({ tenantId });
        if (filters.mine) params.append('mine', 'true');
        if (filters.stage) params.append('stage', filters.stage);
        if (filters.minOverdueHours) params.append('minOverdueHours', String(filters.minOverdueHours));
        if (filters.page) params.append('page', String(filters.page));
        if (filters.pageSize) params.append('pageSize', String(filters.pageSize));

        const response = await authorizedFetch(`${API_BASE.WORKFLOW}/api/stall-queue?${params.toString()}`, {
            method: 'GET',
            headers: { 'X-Tenant-ID': tenantId },
        });
        const items = (await response.json()) as StallQueueItem[];
        const total = Number(response.headers.get('X-Total-Count'));
        return { items, totalCount: Number.isFinite(total) && total > 0 ? total : items.length };
    },

    // CSTD-35: intervention & recovery
    async recordIntervention(
        engagementId: string,
        data: RecordInterventionRequest,
        tenantId: string
    ): Promise<Intervention> {
        return request<Intervention>(
            `${API_BASE.WORKFLOW}/api/engagements/${engagementId}/interventions?tenantId=${encodeURIComponent(tenantId)}`,
            {
                method: 'POST',
                headers: { 'X-Tenant-ID': tenantId },
                body: JSON.stringify(data),
            }
        );
    },

    // Owner only: hand the engagement to another staff member / owner (staff only see engagements they are responsible for).
    async changeResponsibleStaff(engagementId: string, staffId: string): Promise<Engagement> {
        return request<Engagement>(`${API_BASE.WORKFLOW}/api/Engagements/${engagementId}/staff`, {
            method: 'PUT',
            body: JSON.stringify({ staffId }),
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

    // CSTD-19: Next action for the staff workspace: GET /api/engagements/{id}/next-action (Owner/Staff).
    // Computed on every read from live state (no cache), so re-fetch after any change.
    async getNextAction(engagementId: string, tenantId: string): Promise<NextActionResult> {
        return request<NextActionResult>(
            `${API_BASE.WORKFLOW}/api/engagements/${engagementId}/next-action?tenantId=${encodeURIComponent(tenantId)}`,
            {
                method: 'GET',
                headers: { 'X-Tenant-ID': tenantId },
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

    // What the standard checklist would add right now (nothing is saved) — shown for review before applying.
    async previewStandardChecklist(engagementId: string, tenantId: string): Promise<ClientAction[]> {
        return request<ClientAction[]>(
            `${API_BASE.WORKFLOW}/api/engagements/${engagementId}/actions/standard-checklist/preview?tenantId=${encodeURIComponent(tenantId)}`,
            {
                method: 'GET',
                headers: { 'X-Tenant-ID': tenantId },
            });
    },

    // Staff-defined stage tasks: opt-in standard checklist (idempotent). Returns only the tasks it added.
    async applyStandardChecklist(engagementId: string, tenantId: string): Promise<ClientAction[]> {
        return request<ClientAction[]>(
            `${API_BASE.WORKFLOW}/api/engagements/${engagementId}/actions/standard-checklist?tenantId=${encodeURIComponent(tenantId)}`,
            {
                method: 'POST',
                headers: { 'X-Tenant-ID': tenantId },
            });
    },

    // Edit a Pending, staff-managed task (Owner/Staff). 409 if not Pending or linked to a requirement/condition.
    async updateAction(engagementId: string, actionId: string, req: UpdateClientActionRequest, tenantId: string): Promise<ClientAction> {
        return request<ClientAction>(
            `${API_BASE.WORKFLOW}/api/engagements/${engagementId}/actions/${actionId}?tenantId=${encodeURIComponent(tenantId)}`,
            {
                method: 'PATCH',
                headers: { 'X-Tenant-ID': tenantId },
                body: JSON.stringify(req),
            });
    },

    // Cancel a task that is no longer required (kept as Cancelled). 409 for completed or source-linked tasks.
    async cancelAction(engagementId: string, actionId: string, reason: string, tenantId: string): Promise<ClientAction> {
        return request<ClientAction>(
            `${API_BASE.WORKFLOW}/api/engagements/${engagementId}/actions/${actionId}/cancel?tenantId=${encodeURIComponent(tenantId)}`,
            {
                method: 'PUT',
                headers: { 'X-Tenant-ID': tenantId },
                body: JSON.stringify({ reason }),
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
    // CSTD-16 staff side: ask the client for information (creates the requirement and its task).
    async requestRequirement(engagementId: string, data: RequestRequirementRequest, tenantId: string): Promise<RequirementResponse> {
        return request<RequirementResponse>(
            `${API_BASE.WORKFLOW}/api/engagements/${engagementId}/requirements?tenantId=${encodeURIComponent(tenantId)}`,
            {
                method: 'POST',
                headers: { 'X-Tenant-ID': tenantId },
                body: JSON.stringify(data),
            }
        );
    },

    // Staff view (includes the client's answer and review metadata).
    async getRequirements(engagementId: string, tenantId: string): Promise<RequirementResponse[]> {
        return request<RequirementResponse[]>(
            `${API_BASE.WORKFLOW}/api/engagements/${engagementId}/requirements?tenantId=${encodeURIComponent(tenantId)}&isClientView=false`,
            {
                method: 'GET',
                headers: { 'X-Tenant-ID': tenantId },
            }
        );
    },

    // Approve the client's answer, or reject it with a reason (the client can then resubmit).
    async reviewRequirement(
        engagementId: string,
        requirementId: string,
        data: ReviewRequirementRequest,
        tenantId: string
    ): Promise<RequirementResponse> {
        return request<RequirementResponse>(
            `${API_BASE.WORKFLOW}/api/engagements/${engagementId}/requirements/${requirementId}/review?tenantId=${encodeURIComponent(tenantId)}`,
            {
                method: 'PUT',
                headers: { 'X-Tenant-ID': tenantId },
                body: JSON.stringify(data),
            }
        );
    },

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

    async listMeetings(engagementId: string, tenantId: string): Promise<Meeting[]> {
        return request<Meeting[]>(
            `${API_BASE.WORKFLOW}/api/engagements/${engagementId}/meetings?tenantId=${encodeURIComponent(tenantId)}`,
            { headers: { 'X-Tenant-ID': tenantId } });
    },

    async createMeeting(engagementId: string, req: CreateMeetingRequest, tenantId: string): Promise<Meeting> {
        return request<Meeting>(
            `${API_BASE.WORKFLOW}/api/engagements/${engagementId}/meetings?tenantId=${encodeURIComponent(tenantId)}`,
            { method: 'POST', headers: { 'X-Tenant-ID': tenantId }, body: JSON.stringify(req) });
    },

    async updateMeeting(engagementId: string, meetingId: string, req: UpdateMeetingRequest, tenantId: string): Promise<Meeting> {
        return request<Meeting>(
            `${API_BASE.WORKFLOW}/api/engagements/${engagementId}/meetings/${meetingId}?tenantId=${encodeURIComponent(tenantId)}`,
            { method: 'PUT', headers: { 'X-Tenant-ID': tenantId }, body: JSON.stringify(req) });
    },

    async updateMeetingStatus(engagementId: string, meetingId: string, status: string, tenantId: string): Promise<Meeting> {
        return request<Meeting>(
            `${API_BASE.WORKFLOW}/api/engagements/${engagementId}/meetings/${meetingId}/status?tenantId=${encodeURIComponent(tenantId)}`,
            { method: 'PUT', headers: { 'X-Tenant-ID': tenantId }, body: JSON.stringify({ status }) });
    },

    async rescheduleMeeting(engagementId: string, meetingId: string, req: RescheduleMeetingRequest, tenantId: string): Promise<Meeting> {
        return request<Meeting>(
            `${API_BASE.WORKFLOW}/api/engagements/${engagementId}/meetings/${meetingId}/reschedule?tenantId=${encodeURIComponent(tenantId)}`,
            { method: 'POST', headers: { 'X-Tenant-ID': tenantId }, body: JSON.stringify(req) });
    },

    async listMissedMeetings(tenantId: string): Promise<Meeting[]> {
        return request<Meeting[]>(
            `${API_BASE.WORKFLOW}/api/meetings/missed?tenantId=${encodeURIComponent(tenantId)}`,
            { headers: { 'X-Tenant-ID': tenantId } });
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
    // The Audit API lives under /api (previously called without it, so every request returned 404).
    async getEvents(tenantId?: string, engagementId?: string): Promise<AuditEvent[]> {
        const base = engagementId
            ? `${API_BASE.AUDIT}/api/audit-events/engagement/${encodeURIComponent(engagementId)}`
            : `${API_BASE.AUDIT}/api/audit-events`;
        const url = tenantId ? `${base}?tenantId=${encodeURIComponent(tenantId)}` : base;
        return request<AuditEvent[]>(url);
    },

    // Hash chains are per engagement (CSTD-40), so verification takes one engagement at a time.
    async verifyChain(engagementId: string, tenantId?: string): Promise<ChainVerificationResult> {
        const params = new URLSearchParams({ engagementId });
        if (tenantId) params.append('tenantId', tenantId);
        return request<ChainVerificationResult>(`${API_BASE.AUDIT}/api/audit-events/verify?${params.toString()}`);
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

    /**
     * Downloads a document with the caller's token and saves it under fileName. The download endpoint
     * requires authentication, so a plain link (which sends no Authorization header) gets a 401.
     */
    async downloadDocument(
        engagementId: string,
        documentId: string,
        fileName: string,
        tenantId?: string,
        includeDeleted: boolean = false
    ): Promise<void> {
        const response = await authorizedFetch(
            DocumentsApi.getDownloadUrl(engagementId, documentId, tenantId, includeDeleted),
            { headers: tenantId ? { 'X-Tenant-ID': tenantId } : undefined }
        );
        const blob = await response.blob();
        const objectUrl = URL.createObjectURL(blob);
        const link = document.createElement('a');
        link.href = objectUrl;
        link.download = fileName || `document-${documentId}`;
        document.body.appendChild(link);
        link.click();
        link.remove();
        // Give the browser a moment to start the download before releasing the blob.
        setTimeout(() => URL.revokeObjectURL(objectUrl), 10_000);
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

