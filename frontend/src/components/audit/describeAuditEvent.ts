import { AuditEvent } from '../../types';
import { parseApiJson } from '../../services/api';
import { engagementStatusLabel } from '../../utils/displayLabels';

/**
 * Turns a raw audit event (type code + JSON payload) into plain language: a label, a category and
 * detail lines that say exactly what changed. Pure; names and task titles are supplied by the caller.
 */

export type AuditCategory = 'Engagement' | 'Tasks' | 'Questions' | 'Documents' | 'Conditions' | 'Meetings' | 'Interventions';

export type AuditTone = 'neutral' | 'positive' | 'negative' | 'warning' | 'info';

export interface AuditDescription {
    category: AuditCategory;
    label: string;
    /** Short "what changed" lines, e.g. `Status: Pending → Completed`. */
    details: string[];
    tone: AuditTone;
}

export interface AuditLookups {
    /** Title of a task by actionId, for older events that only carry the id. */
    taskTitle: (actionId?: string | null) => string | undefined;
    /** Display name (email) of a user id, when the team list is available. */
    personName?: (userId?: string | null) => string | undefined;
}

type Payload = Record<string, any>;

export const parsePayload = (evt: AuditEvent): Payload => {
    try {
        // Same UTC handling as API responses, so times inside event details show in local time.
        const parsed = parseApiJson(evt.payload || '{}');
        return parsed && typeof parsed === 'object' ? parsed : {};
    } catch {
        return {};
    }
};

/** "DocumentCollection" -> "Document Collection". */
export const humanize = (value?: string | null): string =>
    value ? String(value).replace(/([a-z])([A-Z])/g, '$1 $2').replace(/_/g, ' ') : '';

const TASK_TYPE_LABELS: Record<string, string> = {
    CustomTask: 'Custom task',
    KycDocument: 'KYC document',
    SignAgreement: 'Sign agreement',
    ProofOfAddress: 'Proof of address',
    DocumentUpload: 'Document upload',
    Requirement: 'Request information',
    Approval: 'Approval',
    Payment: 'Payment',
};

const typeLabel = (t?: string | null) => (t ? TASK_TYPE_LABELS[t] ?? humanize(t) : '');

const formatDate = (value?: string | null) => {
    if (!value) return 'none';
    const d = new Date(value);
    return Number.isNaN(d.getTime()) ? value : d.toLocaleDateString();
};

/** Date and time in the viewer's local time, e.g. for a meeting slot. */
const formatDateTime = (value?: string | null) => {
    if (!value) return undefined;
    const d = new Date(value);
    return Number.isNaN(d.getTime())
        ? value
        : d.toLocaleString([], { dateStyle: 'medium', timeStyle: 'short' });
};

const FIELD_LABELS: Record<string, string> = {
    title: 'Title',
    description: 'Description',
    deadlineUtc: 'Deadline',
    stageNumber: 'Stage',
    assignedToRole: 'Assigned to',
    isInternalOnly: 'Internal only',
    type: 'Type',
};

const formatFieldValue = (field: string, value: any): string => {
    if (value === null || value === undefined || value === '') return 'none';
    switch (field) {
        case 'deadlineUtc':
            return formatDate(value);
        case 'type':
            return typeLabel(value);
        case 'isInternalOnly':
            return value === true || value === 'true' ? 'yes' : 'no';
        case 'title':
        case 'description':
            return `"${value}"`;
        default:
            return String(value);
    }
};

const quoted = (title?: string | null) => (title ? `"${title}"` : undefined);

const compact = (lines: (string | undefined | false | null)[]) => lines.filter(Boolean) as string[];

export function describeAuditEvent(evt: AuditEvent, lookups: AuditLookups): AuditDescription {
    // Never print a raw user id: unknown people (or staff the viewer can't list) stay anonymous.
    const staffName = (id?: string | null) => (id ? lookups.personName?.(id) ?? 'a team member' : 'unassigned');
    const p = parsePayload(evt);
    const taskName = quoted(p.title ?? p.actionTitle ?? lookups.taskTitle(p.actionId ?? p.blockerActionId));

    switch (evt.type) {
        // ---------------- Engagement ----------------
        case 'Genesis':
            return { category: 'Engagement', label: 'Engagement created', details: compact([p.status && `Status: ${engagementStatusLabel(p.status)}`, p.stage && `Stage: ${humanize(p.stage)}`]), tone: 'info' };
        case 'StatusChange': {
            const to = p.toStatus as string | undefined;
            const label = to === 'Started' ? 'Engagement started' : to === 'Closed' ? 'Engagement closed' : to === 'Cancelled' ? 'Engagement cancelled' : 'Engagement status changed';
            return { category: 'Engagement', label, details: compact([`Status: ${engagementStatusLabel(p.fromStatus) || '?'} → ${engagementStatusLabel(to) || '?'}`]), tone: to === 'Cancelled' ? 'negative' : 'positive' };
        }
        case 'ResponsibleStaffChanged':
            return {
                category: 'Engagement',
                label: 'Responsible staff changed',
                details: compact([`Staff: ${staffName(p.fromStaffId)} → ${staffName(p.toStaffId)}`]),
                tone: 'info',
            };
        case 'StageChange':
            return { category: 'Engagement', label: 'Stage advanced', details: [`Stage: ${humanize(p.fromStage)} → ${humanize(p.toStage)}`], tone: 'positive' };

        // ---------------- Tasks ----------------
        case 'ClientActionCreated':
            return {
                category: 'Tasks',
                label: 'Task added',
                details: compact([
                    taskName,
                    [p.stageNumber && `Stage ${p.stageNumber}`, typeLabel(p.type), p.assignedToRole && `for ${p.assignedToRole}`].filter(Boolean).join(' · '),
                    p.deadlineUtc && `Due ${formatDate(p.deadlineUtc)}`,
                ]),
                tone: 'info',
            };
        case 'ClientActionUpdated': {
            const changes: { field: string; from: any; to: any }[] = Array.isArray(p.changes) ? p.changes : [];
            const lines = changes.length > 0
                ? changes.map((c) => `${FIELD_LABELS[c.field] ?? humanize(c.field)}: ${formatFieldValue(c.field, c.from)} → ${formatFieldValue(c.field, c.to)}`)
                : Array.isArray(p.changedFields) && p.changedFields.length > 0
                ? [`Changed: ${p.changedFields.map((f: string) => (FIELD_LABELS[f] ?? humanize(f)).toLowerCase()).join(', ')}`]
                : [];
            return { category: 'Tasks', label: 'Task edited', details: compact([taskName, ...lines]), tone: 'neutral' };
        }
        case 'ClientActionStatusChanged': {
            const to = p.toStatus as string | undefined;
            const label =
                to === 'Completed' ? 'Task completed'
                : to === 'Uploaded' ? 'Evidence uploaded'
                : to === 'Rejected' ? 'Task sent back to the client'
                : to === 'Cancelled' ? 'Task cancelled'
                : to === 'Pending' ? 'Task reopened'
                : 'Task status changed';
            const tone: AuditTone = to === 'Completed' ? 'positive' : to === 'Rejected' || to === 'Cancelled' ? 'negative' : 'info';
            return {
                category: 'Tasks',
                label,
                details: compact([taskName, `Status: ${p.fromStatus ?? '?'} → ${to ?? '?'}`, p.reason && p.reason !== 'ActionCompleted' && `Reason: ${humanize(p.reason)}`]),
                tone,
            };
        }
        case 'StandardChecklistApplied': {
            const count = Array.isArray(p.actionIds) ? p.actionIds.length : Array.isArray(p.tasks) ? p.tasks.length : 0;
            const stages = Array.isArray(p.stageNumbers) ? p.stageNumbers.join(', ') : '';
            return {
                category: 'Tasks',
                label: 'Standard checklist applied',
                details: compact([`${count} task${count === 1 ? '' : 's'} added${stages ? ` to stage ${stages}` : ''}`]),
                tone: 'info',
            };
        }
        case 'action.overdue':
            return {
                category: 'Tasks',
                label: 'Task overdue',
                details: compact([taskName, p.hoursOverdue !== undefined && `${p.hoursOverdue}h past the deadline (${formatDate(p.deadlineUtc)})`]),
                tone: 'warning',
            };
        case 'StallResolved':
            return { category: 'Tasks', label: 'Stall resolved', details: compact([taskName, p.resolution && `Resolution: ${humanize(p.resolution)}`]), tone: 'positive' };

        // ---------------- Questions (Request Information) ----------------
        case 'RequirementRequested':
            return { category: 'Questions', label: 'Question asked', details: compact([quoted(p.title) ?? humanize(p.requirementType), p.stageNumber && `Stage ${p.stageNumber}`]), tone: 'info' };
        case 'RequirementSubmitted':
            return { category: 'Questions', label: 'Client answered', details: compact([quoted(p.title) ?? humanize(p.requirementType)]), tone: 'info' };
        case 'RequirementReviewed': {
            const approved = p.decision === 'Approved';
            return {
                category: 'Questions',
                label: approved ? 'Answer approved' : 'Answer rejected',
                details: compact([quoted(p.title), !approved && p.rejectionReason && `Reason: ${p.rejectionReason}`]),
                tone: approved ? 'positive' : 'negative',
            };
        }

        // ---------------- Documents ----------------
        case 'document.uploaded':
            return {
                category: 'Documents',
                label: 'Document uploaded',
                details: compact([
                    quoted(p.fileName),
                    p.documentType && `Type: ${humanize(p.documentType)}`,
                    p.complianceStatus && `Automatic check: ${p.complianceStatus}`,
                    p.rejectionReason && `Reason: ${p.rejectionReason}`,
                ]),
                tone: p.complianceStatus === 'Rejected' ? 'warning' : 'info',
            };
        case 'document.verified':
            return { category: 'Documents', label: 'Document verified', details: compact([quoted(p.fileName) ?? humanize(p.documentType), p.notes && `Notes: ${p.notes}`]), tone: 'positive' };
        case 'document.verification_rejected':
            return { category: 'Documents', label: 'Document rejected', details: compact([quoted(p.fileName) ?? humanize(p.documentType), p.rejectionReason && `Reason: ${p.rejectionReason}`]), tone: 'negative' };
        case 'document.metadata_updated':
            return { category: 'Documents', label: 'Document details edited', details: compact([quoted(p.fileName) ?? humanize(p.documentType)]), tone: 'neutral' };
        case 'document.soft_deleted':
            return { category: 'Documents', label: 'Document deleted', details: compact([quoted(p.fileName) ?? humanize(p.documentType), p.reason && `Reason: ${p.reason}`]), tone: 'negative' };

        // ---------------- Conditions ----------------
        case 'ApprovalAttached':
            return {
                category: 'Conditions',
                label: 'Approval requested',
                details: compact([
                    quoted(p.title),
                    p.requiredBeforeStage && `Blocks: ${humanize(p.requiredBeforeStage)}`,
                    p.dueDateUtc && `Due ${formatDate(p.dueDateUtc)}`,
                ]),
                tone: 'info',
            };
        case 'ApprovalCompleted':
            return {
                category: 'Conditions',
                label: 'Approval completed',
                details: compact([
                    quoted(p.title),
                    p.decisionAt && `Decided ${formatDate(p.decisionAt)}`,
                ]),
                tone: 'positive',
            };
        case 'ApprovalRejected':
            return {
                category: 'Conditions',
                label: 'Approval rejected',
                details: compact([
                    quoted(p.title),
                    p.rejectionReason && `Reason: ${p.rejectionReason}`,
                    p.decisionAt && `Decided ${formatDate(p.decisionAt)}`,
                ]),
                tone: 'negative',
            };
        case 'ConditionAttached':
            return {
                category: 'Conditions',
                label: `${humanize(p.type) || 'Condition'} condition attached`,
                details: compact([
                    quoted(p.title),
                    p.amount !== undefined && p.amount !== null && `Amount: ${p.amount} ${p.currency ?? ''}`.trim(),
                    p.requiredBeforeStage && `Blocks: ${humanize(p.requiredBeforeStage)}`,
                    p.dueDateUtc && `Due ${formatDate(p.dueDateUtc)}`,
                ]),
                tone: 'info',
            };
        case 'ConditionUpdated': {
            const status = p.status as string | undefined;
            const label = status === 'Satisfied' ? 'Condition satisfied' : status === 'Rejected' ? 'Condition rejected' : 'Condition updated';
            return {
                category: 'Conditions',
                label,
                details: compact([quoted(p.title), p.statusReason && `Reason: ${p.statusReason}`, p.amount !== undefined && p.amount !== null && `Amount: ${p.amount} ${p.currency ?? ''}`.trim()]),
                tone: status === 'Satisfied' ? 'positive' : status === 'Rejected' ? 'negative' : 'neutral',
            };
        }
        case 'ConditionDeactivated':
            return { category: 'Conditions', label: 'Condition removed', details: compact([p.deactivationReason && `Reason: ${p.deactivationReason}`]), tone: 'negative' };

        // ---------------- Engagement lifecycle (CSTD-20) ----------------
        case 'EngagementReady':
            return {
                category: 'Engagement',
                label: 'Engagement handed off',
                details: compact([p.handoffSummary && `Summary: ${p.handoffSummary}`]),
                tone: 'positive',
            };
        case 'EngagementClosed':
            return {
                category: 'Engagement',
                label: 'Engagement sealed and closed',
                details: compact([p.closureSummary && `Summary: ${p.closureSummary}`]),
                tone: 'positive',
            };

        // ---------------- Meetings (CSTD-32) ----------------
        case 'meeting.scheduled':
            return {
                category: 'Meetings',
                label: p.type === 'Intervention' ? 'Intervention meeting scheduled' : 'Meeting scheduled',
                details: compact([
                    quoted(p.purpose),
                    formatDateTime(p.scheduledAtUtc) && `When: ${formatDateTime(p.scheduledAtUtc)}`,
                    p.importance === 'Important' && 'Marked important',
                ]),
                tone: 'info',
            };
        case 'meeting.rescheduled':
            return {
                category: 'Meetings',
                label: 'Meeting rescheduled',
                details: compact([
                    formatDateTime(p.newScheduledAtUtc) && `New time: ${formatDateTime(p.newScheduledAtUtc)}`,
                    p.reason && `Reason: ${p.reason}`,
                ]),
                tone: 'warning',
            };
        case 'meeting.completed':
        case 'meeting.missed':
        case 'meeting.cancelled':
        case 'meeting.updated': {
            const outcome = evt.type.split('.')[1];
            const labels: Record<string, string> = {
                completed: 'Meeting completed',
                missed: 'Meeting marked as missed',
                cancelled: 'Meeting cancelled',
                updated: 'Meeting updated',
            };
            const tones: Record<string, AuditTone> = { completed: 'positive', missed: 'negative', cancelled: 'neutral', updated: 'neutral' };
            return {
                category: 'Meetings',
                label: labels[outcome],
                details: compact([
                    formatDateTime(p.scheduledAtUtc) && `Scheduled for: ${formatDateTime(p.scheduledAtUtc)}`,
                    p.importance === 'Important' && 'Important meeting',
                ]),
                tone: tones[outcome],
            };
        }

        // ---------------- Interventions (CSTD-35) ----------------
        case 'intervention.recovered': {
            const outcome = p.outcome as string | undefined;
            const tone: AuditTone =
                outcome === 'Recovered' ? 'positive' : outcome === 'Escalated' ? 'warning' : outcome === 'Progressing' ? 'info' : 'neutral';
            return {
                category: 'Interventions',
                label: p.type === 'Meeting' ? 'Intervention meeting recorded' : 'Recovery action recorded',
                details: compact([
                    taskName && `Blocked task: ${taskName}`,
                    outcome && `Outcome: ${humanize(outcome)}`,
                    p.reason && `Reason: ${p.reason}`,
                    (outcome === 'Recovered' || outcome === 'Progressing') && 'Client notified',
                ]),
                tone,
            };
        }

        // ---------------- Audit Integrity (CSTD-42) ----------------
        case 'AuditEventFlagged':
            return {
                category: 'Engagement',
                label: 'Audit event flagged',
                details: compact([
                    p.reason && `Reason: ${p.reason}`,
                    p.referencedEventType && `Referenced event: ${humanize(p.referencedEventType)} (#${p.referencedSequenceNumber})`,
                ]),
                tone: 'warning',
            };

        default:
            return { category: 'Engagement', label: humanize(evt.type), details: [], tone: 'neutral' };
    }
}

/** Task ids an event refers to, so older events (without titles) can be named from the task list. */
export const referencedActionId = (evt: AuditEvent): string | undefined => {
    const p = parsePayload(evt);
    return p.title || p.actionTitle ? undefined : (p.actionId ?? p.blockerActionId) ?? undefined;
};
