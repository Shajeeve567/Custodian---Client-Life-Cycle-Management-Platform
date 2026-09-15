/**
 * Maps a ClientAction's Type (the task-tracking vocabulary: "KycDocument", "SignAgreement",
 * "ProofOfAddress", ...) to the Documents service's own Type vocabulary ("KYC_PASSPORT",
 * "SIGNED_AGREEMENT", "PROOF_OF_ADDRESS", ...) — the same ALL_CAPS strings
 * GateRequirements.cs (backend, CSTD-18) matches against.
 *
 * These are two genuinely different vocabularies for two different concepts (what task is this
 * vs. what kind of document satisfies it), and they must stay in sync in exactly one place —
 * every previous case of them drifting (UploadEvidenceModal uploading under the wrong Type,
 * WorkspaceStageView's findLinkedDoc looking up the wrong Type) came from each call site
 * re-deriving this mapping independently instead of sharing it.
 */
export const ACTION_TYPE_TO_DOCUMENT_TYPE: Record<string, string> = {
    KycDocument: 'KYC_PASSPORT',
    SignAgreement: 'SIGNED_AGREEMENT',
    ProofOfAddress: 'PROOF_OF_ADDRESS',
};

/**
 * Resolves the Documents-service Type to upload/match against for a given action.
 * Falls back to `fallback` (e.g. a user-selected dropdown value) when the action's Type has no
 * known document-type mapping — covers generic "DocumentUpload"/"CustomTask" actions.
 */
export function resolveDocumentType(actionType: string | undefined | null, fallback: string): string {
    if (actionType && ACTION_TYPE_TO_DOCUMENT_TYPE[actionType]) {
        return ACTION_TYPE_TO_DOCUMENT_TYPE[actionType];
    }
    return fallback;
}
