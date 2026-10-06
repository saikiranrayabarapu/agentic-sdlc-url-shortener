/** Formats a millisecond duration as `1,234 ms (1.2 s)`; `n/a` when absent. */
export function formatMs(value: number | null | undefined): string {
  if (value === null || value === undefined || Number.isNaN(value)) return 'n/a';
  const ms = Math.round(value).toLocaleString();
  return value >= 1000 ? `${ms} ms (${(value / 1000).toFixed(1)} s)` : `${ms} ms`;
}

/** Maps an audit event type to a visual tone, mirroring the CLI's console colouring. */
export function auditTone(type: string): 'danger' | 'warn' | 'purple' | 'info' | 'ok' | 'dim' | 'none' {
  switch (type) {
    case 'NodeFailed':
    case 'PolicyViolation':
    case 'Rollback':
      return 'danger';
    case 'Retry':
    case 'RepairCycle':
    case 'Warning':
    case 'Fallback':
      return 'warn';
    case 'ApprovalRequested':
    case 'ClarificationRequested':
    case 'SafeStop':
      return 'purple';
    case 'ApprovalDecided':
    case 'ClarificationAnswered':
    case 'WorkflowReplanned':
      return 'info';
    case 'NodeSucceeded':
    case 'Metrics':
      return 'ok';
    case 'LlmCall':
    case 'EntryGate':
    case 'ExitGate':
    case 'Snapshot':
    case 'ToolRun':
    case 'WorkspaceWrite':
      return 'dim';
    default:
      return 'none';
  }
}
