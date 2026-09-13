import { EngagementStage } from '../types';

/**
 * The 5 canonical engagement stages (CSTD-17), in pipeline order.
 * Mirrors the backend's EngagementStage enum exactly (Workflow service,
 * backend/src/services/Workflow/Models/EngagementStage.cs) — the `key` values
 * are the literal strings the API sends/accepts, not display labels.
 */
export interface StageDefinition {
    key: EngagementStage;
    order: number; // 1-based position in the pipeline
    name: string;
    tagline: string;
    description: string;
}

export const ENGAGEMENT_STAGES: StageDefinition[] = [
    {
        key: 'Onboarding',
        order: 1,
        name: 'Onboarding',
        tagline: 'Client baseline & kickoff criteria',
        description: 'Establish initial client identity, assign a lead custodian, and collect service parameters to kick off the engagement.',
    },
    {
        key: 'DocumentCollection',
        order: 2,
        name: 'Document Collection',
        tagline: 'Gather required compliance documents',
        description: 'Collect mandatory compliance documents from the client and securely store proofs in the Document Vault.',
    },
    {
        key: 'Verification',
        order: 3,
        name: 'Verification',
        tagline: 'Deterministic document & KYC validation',
        description: 'Verify corporate status, validate submitted documents, and confirm all compliance evidence meets requirements.',
    },
    {
        key: 'Execution',
        order: 4,
        name: 'Execution',
        tagline: 'Active delivery & milestone tracking',
        description: 'Execute the engagement scope of work, track delivery milestones, and monitor SLA adherence.',
    },
    {
        key: 'Closure',
        order: 5,
        name: 'Closure',
        tagline: 'Final delivery & handoff',
        description: 'Compile final deliverables, hand off client access, and formally close out the engagement.',
    },
];

const STAGE_ORDER: EngagementStage[] = ENGAGEMENT_STAGES.map((s) => s.key);

export function getStageDefinition(stage: EngagementStage | undefined | null): StageDefinition {
    return ENGAGEMENT_STAGES.find((s) => s.key === stage) || ENGAGEMENT_STAGES[0];
}

/** 0-based index of a stage in the pipeline (-1 if unrecognized). */
export function getStageIndex(stage: EngagementStage | undefined | null): number {
    if (!stage) return -1;
    return STAGE_ORDER.indexOf(stage);
}

/** The next stage in sequence, or null if already at the final stage (Closure) or unrecognized. */
export function getNextStage(stage: EngagementStage): EngagementStage | null {
    const idx = getStageIndex(stage);
    if (idx === -1 || idx >= STAGE_ORDER.length - 1) return null;
    return STAGE_ORDER[idx + 1];
}
