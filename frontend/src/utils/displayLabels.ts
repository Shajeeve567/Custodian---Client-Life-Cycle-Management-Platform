/**
 * Human-readable labels for the backend's internal values (enum names, ALL_CAPS document types,
 * user GUIDs). Every screen goes through these helpers so raw identifiers never reach users.
 */
import { EngagementStage, UserAccountResponse } from '../types';
import { getStageDefinition } from '../constants/engagementStages';

/** "DocumentCollection" / "KYC_PASSPORT" / "non-compliant" -> "Document Collection" / "Kyc Passport" / "Non Compliant". */
export function humanize(value: string | null | undefined): string {
    if (!value) return '';
    const spaced = value
        .replace(/[_-]+/g, ' ')
        .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
        .replace(/([A-Z]+)([A-Z][a-z])/g, '$1 $2')
        .trim()
        .toLowerCase();
    return spaced.replace(/\b\w/g, (c) => c.toUpperCase());
}

const GUID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

export function isGuid(value: string | null | undefined): boolean {
    return Boolean(value && GUID_PATTERN.test(value));
}

/** Short human reference for an engagement, e.g. "ENG-3F2A1B". */
export function engagementCode(engagementId: string | null | undefined): string {
    return engagementId ? `ENG-${engagementId.slice(0, 6).toUpperCase()}` : 'ENG-—';
}

const TASK_TYPE_LABELS: Record<string, string> = {
    CustomTask: 'General task',
    KycDocument: 'KYC document',
    SignAgreement: 'Signed agreement',
    ProofOfAddress: 'Proof of address',
    DocumentUpload: 'Document upload',
    RequestInformation: 'Question',
    ClientInformation: 'Information request',
    StallResolved: 'Follow-up',
};

export function taskTypeLabel(type: string | null | undefined): string {
    if (!type) return 'Task';
    return TASK_TYPE_LABELS[type] ?? humanize(type);
}

const TASK_SOURCE_LABELS: Record<string, string> = {
    Manual: 'Added by staff',
    Lifecycle: 'Standard checklist',
    Requirement: 'Question',
    Document: 'Document',
    Condition: 'Approval / payment',
    Meeting: 'Meeting',
};

/** Where a task came from. Meant for staff screens only; clients don't need this detail. */
export function taskSourceLabel(sourceType: string | null | undefined): string {
    if (!sourceType) return TASK_SOURCE_LABELS.Manual;
    return TASK_SOURCE_LABELS[sourceType] ?? humanize(sourceType);
}

const DOCUMENT_TYPE_LABELS: Record<string, string> = {
    KYC_PASSPORT: 'Passport / photo ID',
    SIGNED_AGREEMENT: 'Signed agreement',
    PROOF_OF_ADDRESS: 'Proof of address',
    CERTIFICATE_OF_INCORPORATION: 'Certificate of incorporation',
    TAX_DECLARATION: 'Tax declaration',
    COMPLIANCE_EVIDENCE: 'Compliance evidence',
    CORPORATE_REGISTRY: 'Corporate registry',
    BANK_STATEMENT: 'Bank statement',
};

export function documentTypeLabel(type: string | null | undefined): string {
    if (!type) return 'Document';
    const known = DOCUMENT_TYPE_LABELS[type.toUpperCase()];
    if (known) return known;
    const label = humanize(type);
    return label.charAt(0) + label.slice(1).toLowerCase();
}

export function stageLabel(stage: string | null | undefined): string {
    if (!stage) return '';
    const def = getStageDefinition(stage as EngagementStage);
    return def.key === stage ? def.name : humanize(stage);
}

const ENGAGEMENT_STATUS_LABELS: Record<string, string> = {
    Draft: 'Draft',
    Started: 'In progress',
    Closed: 'Completed',
    Cancelled: 'Cancelled',
};

export function engagementStatusLabel(status: string | null | undefined): string {
    if (!status) return '';
    return ENGAGEMENT_STATUS_LABELS[status] ?? humanize(status);
}

const COMPLIANCE_STATUS_LABELS: Record<string, string> = {
    Compliant: 'Compliant',
    NonCompliant: 'Non-compliant',
    Rejected: 'Non-compliant',
    Pending: 'Check pending',
};

export function complianceStatusLabel(status: string | null | undefined): string {
    if (!status) return '';
    return COMPLIANCE_STATUS_LABELS[status] ?? humanize(status);
}

/** Verification states arrive both as "Verified" and "VERIFIED". */
export function verificationStatusLabel(status: string | null | undefined): string {
    if (!status) return '';
    switch (status.toUpperCase()) {
        case 'VERIFIED': return 'Verified';
        case 'REJECTED': return 'Rejected';
        case 'PENDING': return 'Pending review';
        default: return humanize(status);
    }
}

interface PersonLookup {
    users?: UserAccountResponse[];
    currentUserId?: string | null;
    /** Shown when the id can't be resolved to a person. */
    fallback?: string;
}

/**
 * Turns an actor value (a user GUID, an email, or a service name) into something readable.
 * Unknown GUIDs become the fallback instead of being printed.
 */
export function personLabel(actor: string | null | undefined, lookup: PersonLookup = {}): string {
    const fallback = lookup.fallback ?? 'A team member';
    if (!actor) return fallback;
    if (lookup.currentUserId && actor === lookup.currentUserId) return 'You';
    const user = lookup.users?.find((u) => u.id === actor || u.email === actor);
    if (user) return user.email;
    if (isGuid(actor)) return fallback;
    if (actor.endsWith('-service')) return humanize(actor.replace(/-service$/, '')) + ' service';
    return actor;
}

/** Locale date for ISO dates and plain "YYYY-MM-DD" strings; "—" when missing. */
export function formatDate(value: string | null | undefined): string {
    if (!value) return '—';
    // A bare date is a calendar day; parse it as local time so it never shifts by a day.
    const dateOnly = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value);
    const date = dateOnly ? new Date(+dateOnly[1], +dateOnly[2] - 1, +dateOnly[3]) : new Date(value);
    return isNaN(date.getTime()) ? value : date.toLocaleDateString();
}
