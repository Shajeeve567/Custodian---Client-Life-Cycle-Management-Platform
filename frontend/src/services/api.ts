import {
    Engagement,
    EngagementStatus,
    CreateEngagementRequest,
    ClientAction,
    CreateClientActionRequest,
    AuditEvent,
    DocumentMetadata,
    LoginRequest,
    LoginResponse,
    CreateUserRequest
} from '../types';

const API_BASE = {
    IDENTITY: 'http://localhost:5281/api',
    WORKFLOW: 'http://localhost:5225/api',
    AUDIT: 'http://localhost:5051/api',
    DOCUMENTS: 'http://localhost:5171/api',
};


function getHeaders(tenantId: string = 'tenant-alpha', token?: string): HeadersInit {
    const headers: Record<string, string> = {
        'Content-Type': 'application/json',
        'X-Tenant-Id': tenantId,
    };
    if (token) {
        headers['Authorization'] = `Bearer ${token}`;
    }
    return headers;
}

export const WorkflowApi = {
    async getEngagements(tenantId: string): Promise<Engagement[]> {
        const res = await fetch(`${API_BASE.WORKFLOW}/engagements?tenantId=${tenantId}`, {
            headers: getHeaders(tenantId),
        });
        if (!res.ok) throw new Error('Failed to fetch engagements');
        return res.json();
    },

    async createEngagement(req: CreateEngagementRequest): Promise<Engagement> {
        const res = await fetch(`${API_BASE.WORKFLOW}/engagements`, {
            method: 'POST',
            headers: getHeaders(req.tenantId),
            body: JSON.stringify(req),
        });
        if (!res.ok) throw new Error('Failed to create engagement');
        return res.json();
    },

    async updateStatus(engagementId: string, status: EngagementStatus, tenantId: string): Promise<Engagement> {
        const res = await fetch(`${API_BASE.WORKFLOW}/engagements/${engagementId}/status`, {
            method: 'PUT',
            headers: getHeaders(tenantId),
            body: JSON.stringify({ tenantId, status }),
        });
        if (!res.ok) {
            const err = await res.text();
            throw new Error(err || 'Failed to update engagement status');
        }
        return res.json();
    },

    async deleteEngagement(engagementId: string, tenantId: string): Promise<void> {
        const res = await fetch(`${API_BASE.WORKFLOW}/engagements/${engagementId}`, {
            method: 'DELETE',
            headers: getHeaders(tenantId),
        });
        if (!res.ok) {
            const err = await res.text();
            throw new Error(err || 'Failed to delete engagement');
        }
    },

    async getActions(engagementId: string, tenantId: string): Promise<ClientAction[]> {
        const res = await fetch(`${API_BASE.WORKFLOW}/engagements/${engagementId}/actions`, {
            headers: getHeaders(tenantId),
        });
        if (!res.ok) throw new Error('Failed to fetch actions');
        return res.json();
    },

    async createAction(req: CreateClientActionRequest, tenantId: string): Promise<ClientAction> {
        const res = await fetch(`${API_BASE.WORKFLOW}/engagements/${req.engagementId}/actions`, {
            method: 'POST',
            headers: getHeaders(tenantId),
            body: JSON.stringify(req),
        });
        if (!res.ok) throw new Error('Failed to create action');
        return res.json();
    },

    async completeAction(actionId: string, tenantId: string): Promise<ClientAction> {
        const res = await fetch(`${API_BASE.WORKFLOW}/actions/${actionId}/complete`, {
            method: 'PUT',
            headers: getHeaders(tenantId),
        });
        if (!res.ok) throw new Error('Failed to complete action');
        return res.json();
    }
};

export const AuditApi = {
    async getEvents(tenantId: string, engagementId?: string): Promise<AuditEvent[]> {
        let url = `${API_BASE.AUDIT}/events?tenantId=${tenantId}`;
        if (engagementId) url += `&engagementId=${engagementId}`;
        const res = await fetch(url, { headers: getHeaders(tenantId) });
        if (!res.ok) throw new Error('Failed to fetch audit events');
        return res.json();
    },

    async verifyChain(tenantId: string): Promise<{ isVerified: boolean; count: number }> {
        const res = await fetch(`${API_BASE.AUDIT}/events/verify?tenantId=${tenantId}`, {
            headers: getHeaders(tenantId),
        });
        if (!res.ok) throw new Error('Failed to verify audit log');
        return res.json();
    }
};

export const DocumentsApi = {
    async getDocuments(engagementId: string, tenantId: string): Promise<DocumentMetadata[]> {
        const res = await fetch(`${API_BASE.DOCUMENTS}/engagements/${engagementId}/documents`, {
            headers: getHeaders(tenantId),
        });
        if (!res.ok) throw new Error('Failed to fetch documents');
        return res.json();
    },

    async uploadDocument(engagementId: string, formData: FormData, tenantId: string): Promise<DocumentMetadata> {
        const res = await fetch(`${API_BASE.DOCUMENTS}/engagements/${engagementId}/documents`, {
            method: 'POST',
            headers: {
                'X-Tenant-Id': tenantId,
            },
            body: formData,
        });
        if (!res.ok) {
            const err = await res.text();
            throw new Error(err || 'Failed to upload PDF document');
        }
        return res.json();
    },

    getDownloadUrl(engagementId: string, documentId: string): string {
        return `${API_BASE.DOCUMENTS}/engagements/${engagementId}/documents/${documentId}/download`;
    }
};

export const IdentityApi = {
    async login(req: LoginRequest): Promise<LoginResponse> {
        const res = await fetch(`${API_BASE.IDENTITY}/auth/login`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(req),
        });
        if (!res.ok) {
            const err = await res.text();
            throw new Error(err || 'Login failed. Invalid email or password.');
        }
        return res.json();
    },

    async selectWorkspace(tenantId: string, token: string): Promise<LoginResponse> {
        const res = await fetch(`${API_BASE.IDENTITY}/auth/select-workspace/${tenantId}`, {
            method: 'POST',
            headers: getHeaders(tenantId, token),
        });
        if (!res.ok) {
            const err = await res.text();
            throw new Error(err || 'Workspace selection failed.');
        }
        return res.json();
    },

    async register(req: CreateUserRequest, tenantId: string, token?: string): Promise<any> {
        const res = await fetch(`${API_BASE.IDENTITY}/UserAccount/register`, {
            method: 'POST',
            headers: getHeaders(tenantId, token),
            body: JSON.stringify(req),
        });
        if (!res.ok) {
            const err = await res.text();
            throw new Error(err || 'User registration failed.');
        }
        return res.json();
    }
};

