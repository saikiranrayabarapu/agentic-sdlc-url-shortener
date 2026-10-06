import { StageNode } from '../../api/models';

/**
 * Topological "waves" of the stage graph: nodes in the same wave have no dependencies on each other
 * and may run in parallel. Mirrors WorkflowDefinition.Waves() in the .NET core, but preserves the
 * workflow's declared node order within a wave instead of sorting alphabetically.
 */
export function computeWaves(nodes: StageNode[]): StageNode[][] {
  const known = new Set(nodes.map((n) => n.id));
  const remaining = new Map<string, Set<string>>(
    nodes.map((n) => [n.id, new Set(n.dependsOn.filter((d) => known.has(d)))]),
  );
  const byId = new Map(nodes.map((n) => [n.id, n]));
  const waves: StageNode[][] = [];

  while (remaining.size > 0) {
    const ready = nodes.filter((n) => remaining.get(n.id)?.size === 0).map((n) => n.id);
    if (ready.length === 0) {
      // Cycle (should never happen; the host validates the graph). Emit the rest as one wave.
      waves.push([...remaining.keys()].map((id) => byId.get(id)!));
      break;
    }
    waves.push(ready.map((id) => byId.get(id)!));
    for (const id of ready) remaining.delete(id);
    for (const deps of remaining.values()) for (const id of ready) deps.delete(id);
  }
  return waves;
}
