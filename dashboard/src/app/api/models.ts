/**
 * Typed mirrors of the .NET host's JSON contract.
 * The API serializes with camelCase property names and PascalCase string enums
 * (System.Text.Json + JsonStringEnumConverter) and omits null values.
 */

export type RunStatus =
  | 'Planned'
  | 'Running'
  | 'AwaitingApproval'
  | 'AwaitingClarification'
  | 'Completed'
  | 'Halted'
  | 'RolledBack'
  | 'Failed';

export type NodeStatus =
  | 'Pending'
  | 'Running'
  | 'AwaitingApproval'
  | 'Succeeded'
  | 'Failed'
  | 'Skipped'
  | 'Invalidated';

export type StageKind =
  | 'CodebaseAnalysis'
  | 'Requirements'
  | 'Design'
  | 'Implementation'
  | 'TestAuthoring'
  | 'Verification'
  | 'Documentation'
  | 'OperationalReadiness'
  | 'ReleaseReadiness';

export type ApprovalRule = 'None' | 'Always' | 'IfHighImpact';
export type ImpactLevel = 'Low' | 'Medium' | 'High';
export type Actor = 'System' | 'Agent' | 'Human' | 'Policy';
export type ScenarioKind = 'Greenfield' | 'Brownfield' | 'Ambiguous';

export const TERMINAL_RUN_STATUSES: readonly RunStatus[] = ['Completed', 'Failed', 'RolledBack', 'Halted'];
export const RESUMABLE_RUN_STATUSES: readonly RunStatus[] = ['Halted', 'RolledBack', 'Failed'];
export const STOPPABLE_RUN_STATUSES: readonly RunStatus[] = ['Running', 'AwaitingApproval', 'AwaitingClarification'];

// ---- Workflow -------------------------------------------------------------

export interface StageNode {
  id: string;
  name: string;
  kind: StageKind;
  dependsOn: string[];
  maxRetries: number;
  approval: ApprovalRule;
  exitGate: string;
  entryGate: string;
}

export interface WorkflowDefinition {
  name: string;
  nodes: StageNode[];
  maxRepairCycles: number;
  maxReplans: number;
}

// ---- Run state ------------------------------------------------------------

export interface NodeState {
  nodeId: string;
  status: NodeStatus;
  attempts: number;
  startedAt?: string;
  completedAt?: string;
  lastError?: string;
  feedback?: string;
  artifactIds: string[];
  impact?: ImpactLevel;
  approvalGranted: boolean;
}

export interface Artifact {
  id: string;
  nodeId: string;
  attempt: number;
  type: string;
  summary: string;
  content: unknown;
  inputsHash: string;
  createdAt: string;
  superseded: boolean;
}

export interface Decision {
  id: string;
  at: string;
  actor: Actor;
  actorName?: string;
  nodeId: string;
  /** Approve, Reject, Retry, Repair, Rollback, Replan, Clarify, PolicyBlock, Fallback, SafeStop. */
  kind: string;
  rationale: string;
  basedOnArtifactIds: string[];
}

export interface PendingApproval {
  id: string;
  nodeId: string;
  title: string;
  summary: string;
  impact: ImpactLevel;
  artifactIds: string[];
  requestedAt: string;
}

export interface ClarificationQuestion {
  id: string;
  question: string;
  options: string[];
  defaultAssumption: string;
  impact: ImpactLevel;
}

export interface PendingClarification {
  id: string;
  nodeId: string;
  questions: ClarificationQuestion[];
  requestedAt: string;
}

export interface RecoveryWindow {
  nodeId: string;
  failedAt: string;
  recoveredAt?: string;
  kind: string;
}

export interface RunMetrics {
  nodesAttempted: number;
  nodesSucceeded: number;
  nodesFailed: number;
  retries: number;
  repairCycles: number;
  rollbacks: number;
  fallbacks: number;
  replans: number;
  policyViolations: number;
  approvalsRequested: number;
  approvalsGranted: number;
  llmCalls: number;
  endToEndLatencyMs?: number;
  stageLatencyMs: Record<string, number>;
  recoveries: RecoveryWindow[];
  successRate: number;
  mttrMs?: number;
}

export interface RunState {
  runId: string;
  scenario: string;
  scenarioKind: ScenarioKind;
  mode: string;
  requirementText: string;
  workspacePath: string;
  status: RunStatus;
  createdAt: string;
  updatedAt: string;
  completedAt?: string;
  workflow: WorkflowDefinition;
  nodes: Record<string, NodeState>;
  artifacts: Artifact[];
  decisions: Decision[];
  pendingApprovals: PendingApproval[];
  pendingClarification?: PendingClarification;
  clarificationAnswers: Record<string, string>;
  metrics: RunMetrics;
  repairCyclesUsed: number;
  replansUsed: number;
  warnings: string[];
  haltReason?: string;
  autoApprove: boolean;
  injectFailureNodeId?: string;
  snapshotIds: string[];
}

/** Shape returned by GET /api/runs (see Api.Summary in Program.cs). */
export interface RunSummary {
  runId: string;
  scenario: string;
  scenarioKind: ScenarioKind;
  mode: string;
  status: RunStatus;
  createdAt: string;
  updatedAt: string;
  completedAt?: string;
  haltReason?: string;
  autoApprove: boolean;
  nodes: { nodeId: string; status: NodeStatus; attempts: number; impact?: ImpactLevel }[];
  pendingApprovals: number;
  pendingClarification: boolean;
  metrics: RunMetrics;
}

// ---- Scenarios / modes ----------------------------------------------------

export interface ScenarioWorkflowNode {
  id: string;
  name: string;
  kind: StageKind;
  dependsOn: string[];
  approval: ApprovalRule;
  maxRetries: number;
  entryGate: string;
  exitGate: string;
}

export interface Scenario {
  id: string;
  title: string;
  kind: ScenarioKind;
  requirement: string;
  whatItDemonstrates: string;
  seedFromScenarios: string[];
  workflow: ScenarioWorkflowNode[];
}

// ---- Requests -------------------------------------------------------------

export interface RunRequest {
  scenario: string;
  mode: string;
  autoApprove: boolean;
  injectFailure?: string | null;
  record?: boolean;
}

export interface ApprovalRequest {
  approved: boolean;
  approver: string;
  comment?: string;
}

export interface ClarificationRequest {
  answers: Record<string, string>;
  approver: string;
}

// ---- Audit ----------------------------------------------------------------

export interface AuditEvent {
  seq: number;
  timestamp: string;
  runId: string;
  nodeId?: string;
  attempt?: number;
  type: string;
  actor: Actor;
  actorName?: string;
  message: string;
  data: Record<string, unknown>;
  prevHash: string;
  hash: string;
}

export interface AuditVerifyResult {
  ok: boolean;
  brokenAtSeq?: number | null;
  events: number;
}

// ---- Aggregate metrics (GET /api/metrics) ---------------------------------

export interface ScenarioMetrics {
  runs: number;
  completed: number;
  avgEndToEndMs: number;
}

export interface MetricsAggregate {
  runs: number;
  completed: number;
  runSuccessRate: number;
  nodeSuccessRate: number;
  retries: number;
  repairCycles: number;
  rollbacks: number;
  replans: number;
  policyViolations: number;
  approvalsRequested: number;
  approvalsGranted: number;
  retryFrequency: number;
  rollbackFrequency: number;
  mttrMs?: number | null;
  endToEndLatencyMs: {
    avg?: number | null;
    p50?: number | null;
    p95?: number | null;
  };
  byStatus: Record<string, number>;
  byScenario: Record<string, ScenarioMetrics>;
}

/** Error body returned by the host on 400/409 responses. */
export interface ApiError {
  error: string;
}
