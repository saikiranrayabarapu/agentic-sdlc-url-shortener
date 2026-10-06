import { Component, DestroyRef, ElementRef, OnDestroy, OnInit, computed, inject, signal, viewChild } from '@angular/core';
import { DatePipe, PercentPipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Observable } from 'rxjs';
import { ApiService } from '../../api/api.service';
import {
  Artifact,
  AuditEvent,
  AuditVerifyResult,
  NodeState,
  PendingApproval,
  PendingClarification,
  RESUMABLE_RUN_STATUSES,
  RunState,
  RunStatus,
  STOPPABLE_RUN_STATUSES,
  TERMINAL_RUN_STATUSES,
} from '../../api/models';
import { auditTone, formatMs } from '../../shared/format';
import { computeWaves } from './graph';

const ACTIVE_POLL_MS = 2000;
const TERMINAL_POLL_MS = 5000;
/** Sentinel radio value meaning "use the free-text answer". */
const CUSTOM_ANSWER = '__custom__';

interface ArtifactGroup {
  nodeId: string;
  artifacts: Artifact[];
}

@Component({
  selector: 'app-run-detail-page',
  imports: [DatePipe, PercentPipe, FormsModule, RouterLink],
  templateUrl: './run-detail-page.html',
  styleUrl: './run-detail-page.css',
})
export class RunDetailPage implements OnInit, OnDestroy {
  private readonly api = inject(ApiService);
  private readonly route = inject(ActivatedRoute);
  private readonly destroyRef = inject(DestroyRef);

  readonly CUSTOM = CUSTOM_ANSWER;
  readonly formatMs = formatMs;
  readonly auditTone = auditTone;

  readonly runId = signal('');
  readonly run = signal<RunState | null>(null);
  readonly loadError = signal<string | null>(null);
  readonly actionError = signal<string | null>(null);
  readonly actionNotice = signal<string | null>(null);
  readonly busy = signal(false);

  // Stage graph
  readonly waves = computed(() => computeWaves(this.run()?.workflow.nodes ?? []));

  // Approval / clarification forms
  approver = 'dashboard-user';
  approvalComment = '';
  clarificationSelection: Record<string, string> = {};
  clarificationCustom: Record<string, string> = {};
  private clarificationFormId: string | null = null;

  // Metrics
  readonly stageLatency = computed(() => Object.entries(this.run()?.metrics.stageLatencyMs ?? {}));

  // Artifacts
  readonly selectedArtifactId = signal<string | null>(null);
  readonly artifactsByNode = computed<ArtifactGroup[]>(() => {
    const run = this.run();
    if (!run) return [];
    const order = run.workflow.nodes.map((n) => n.id);
    const groups = new Map<string, Artifact[]>();
    for (const a of run.artifacts) {
      const list = groups.get(a.nodeId) ?? [];
      list.push(a);
      groups.set(a.nodeId, list);
    }
    return [...groups.entries()]
      .sort(([a], [b]) => order.indexOf(a) - order.indexOf(b))
      .map(([nodeId, artifacts]) => ({ nodeId, artifacts: [...artifacts].sort((x, y) => x.attempt - y.attempt) }));
  });
  readonly selectedArtifact = computed(
    () => this.run()?.artifacts.find((a) => a.id === this.selectedArtifactId()) ?? null,
  );
  readonly artifactJson = computed(() => {
    const a = this.selectedArtifact();
    return a ? JSON.stringify(a.content, null, 2) : '';
  });

  // Audit log
  readonly audit = signal<AuditEvent[]>([]);
  readonly verifyResult = signal<AuditVerifyResult | null>(null);
  readonly verifying = signal(false);
  private lastSeq = 0;
  private readonly auditBox = viewChild<ElementRef<HTMLDivElement>>('auditBox');

  // Workspace
  readonly files = signal<string[]>([]);
  readonly selectedFile = signal<string | null>(null);
  readonly fileText = signal<string | null>(null);
  readonly fileError = signal<string | null>(null);
  private lastWorkspaceStamp: string | null = null;

  private pollHandle: ReturnType<typeof setTimeout> | null = null;
  private destroyed = false;

  ngOnInit(): void {
    this.route.paramMap.pipe(takeUntilDestroyed(this.destroyRef)).subscribe((params) => {
      const id = params.get('id') ?? '';
      if (id !== this.runId()) this.load(id);
    });
  }

  ngOnDestroy(): void {
    this.destroyed = true;
    this.clearPoll();
  }

  // ---- Derived state ------------------------------------------------------

  nodeState(run: RunState, nodeId: string): NodeState | undefined {
    return run.nodes[nodeId];
  }

  isTerminal(status: RunStatus): boolean {
    return TERMINAL_RUN_STATUSES.includes(status);
  }

  canStop(): boolean {
    const r = this.run();
    return !!r && STOPPABLE_RUN_STATUSES.includes(r.status);
  }

  canResume(): boolean {
    const r = this.run();
    return !!r && RESUMABLE_RUN_STATUSES.includes(r.status);
  }

  // ---- Loading / polling --------------------------------------------------

  private load(id: string): void {
    this.clearPoll();
    this.runId.set(id);
    this.run.set(null);
    this.loadError.set(null);
    this.actionError.set(null);
    this.actionNotice.set(null);
    this.audit.set([]);
    this.lastSeq = 0;
    this.verifyResult.set(null);
    this.files.set([]);
    this.selectedFile.set(null);
    this.fileText.set(null);
    this.fileError.set(null);
    this.lastWorkspaceStamp = null;
    this.selectedArtifactId.set(null);
    this.clarificationFormId = null;
    this.poll();
  }

  private poll(): void {
    if (this.destroyed) return;
    const id = this.runId();
    if (!id) return;

    this.api.getRun(id).subscribe({
      next: (run) => {
        if (this.destroyed || id !== this.runId()) return;
        this.run.set(run);
        this.loadError.set(null);
        this.syncClarificationForm(run);
        if (run.updatedAt !== this.lastWorkspaceStamp) {
          this.lastWorkspaceStamp = run.updatedAt;
          this.refreshWorkspace(id);
        }
        this.scheduleNext(run.status);
      },
      error: (err) => {
        if (this.destroyed || id !== this.runId()) return;
        this.loadError.set(ApiService.describeError(err));
        this.scheduleNext(null);
      },
    });

    this.api.getAudit(id, this.lastSeq).subscribe({
      next: (events) => {
        if (this.destroyed || id !== this.runId()) return;
        // Guard against overlapping polls (e.g. refreshNow() while a request is in flight) re-delivering events.
        const fresh = events.filter((e) => e.seq > this.lastSeq);
        if (!fresh.length) return;
        this.audit.update((list) => [...list, ...fresh]);
        this.lastSeq = fresh[fresh.length - 1].seq;
        this.scrollAuditToBottom();
      },
    });
  }

  private scheduleNext(status: RunStatus | null): void {
    this.clearPoll();
    if (this.destroyed) return;
    const delay = status && this.isTerminal(status) ? TERMINAL_POLL_MS : ACTIVE_POLL_MS;
    this.pollHandle = setTimeout(() => this.poll(), delay);
  }

  private clearPoll(): void {
    if (this.pollHandle !== null) {
      clearTimeout(this.pollHandle);
      this.pollHandle = null;
    }
  }

  /** Re-fetch immediately (after an action) instead of waiting for the next tick. */
  refreshNow(): void {
    this.clearPoll();
    this.poll();
  }

  private refreshWorkspace(id: string): void {
    this.api.getWorkspaceFiles(id).subscribe({
      next: (files) => {
        if (id !== this.runId()) return;
        this.files.set(files);
        const current = this.selectedFile();
        if (current && files.includes(current)) this.openFile(current);
      },
    });
  }

  private scrollAuditToBottom(): void {
    setTimeout(() => {
      const el = this.auditBox()?.nativeElement;
      if (el) el.scrollTop = el.scrollHeight;
    });
  }

  // ---- Actions ------------------------------------------------------------

  stop(): void {
    this.runAction(this.api.stopRun(this.runId()), 'Stop requested.');
  }

  resume(): void {
    this.runAction(this.api.resumeRun(this.runId()), 'Resume requested.');
  }

  decide(approval: PendingApproval, approved: boolean): void {
    this.runAction(
      this.api.decideApproval(this.runId(), approval.id, {
        approved,
        approver: this.approver.trim() || 'dashboard-user',
        comment: this.approvalComment.trim() || undefined,
      }),
      approved ? 'Approval submitted.' : 'Rejection submitted.',
    );
    this.approvalComment = '';
  }

  submitClarification(pc: PendingClarification): void {
    const answers: Record<string, string> = {};
    for (const q of pc.questions) {
      const selection = this.clarificationSelection[q.id];
      const answer =
        selection === CUSTOM_ANSWER ? (this.clarificationCustom[q.id] ?? '').trim() : (selection ?? '').trim();
      answers[q.id] = answer || q.defaultAssumption;
    }
    this.runAction(
      this.api.answerClarification(this.runId(), pc.id, {
        answers,
        approver: this.approver.trim() || 'dashboard-user',
      }),
      'Clarification answers submitted.',
    );
  }

  verify(): void {
    this.verifying.set(true);
    this.api.verifyAudit(this.runId()).subscribe({
      next: (result) => {
        this.verifying.set(false);
        this.verifyResult.set(result);
      },
      error: (err) => {
        this.verifying.set(false);
        this.actionError.set(ApiService.describeError(err));
      },
    });
  }

  selectArtifact(id: string): void {
    this.selectedArtifactId.set(this.selectedArtifactId() === id ? null : id);
  }

  openFile(path: string): void {
    const changed = path !== this.selectedFile();
    this.selectedFile.set(path);
    this.fileError.set(null);
    // Show "Loading…" when switching files; keep the current text visible when merely refreshing it.
    if (changed) this.fileText.set(null);
    this.api.getWorkspaceFile(this.runId(), path).subscribe({
      next: (text) => {
        if (path === this.selectedFile()) this.fileText.set(text);
      },
      error: (err) => {
        if (path === this.selectedFile()) {
          this.fileText.set('');
          this.fileError.set(ApiService.describeError(err));
        }
      },
    });
  }

  private runAction(request: Observable<unknown>, notice: string): void {
    this.busy.set(true);
    this.actionError.set(null);
    this.actionNotice.set(null);
    request.subscribe({
      next: () => {
        this.busy.set(false);
        this.actionNotice.set(notice);
        setTimeout(() => this.actionNotice.update((n) => (n === notice ? null : n)), 4000);
        this.refreshNow();
      },
      error: (err) => {
        this.busy.set(false);
        this.actionError.set(ApiService.describeError(err));
        this.refreshNow();
      },
    });
  }

  /** Pre-selects the default assumption for each question when a new clarification appears. */
  private syncClarificationForm(run: RunState): void {
    const pc = run.pendingClarification;
    if (!pc) {
      this.clarificationFormId = null;
      return;
    }
    if (this.clarificationFormId === pc.id) return;
    this.clarificationFormId = pc.id;
    const selection: Record<string, string> = {};
    const custom: Record<string, string> = {};
    for (const q of pc.questions) {
      if (q.options.includes(q.defaultAssumption)) {
        selection[q.id] = q.defaultAssumption;
        custom[q.id] = '';
      } else {
        selection[q.id] = CUSTOM_ANSWER;
        custom[q.id] = q.defaultAssumption;
      }
    }
    this.clarificationSelection = selection;
    this.clarificationCustom = custom;
  }
}
