import { Component, DestroyRef, OnInit, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Observable, catchError, forkJoin, of, switchMap, timer } from 'rxjs';
import { ApiService } from '../../api/api.service';
import {
  RESUMABLE_RUN_STATUSES,
  RunSummary,
  STOPPABLE_RUN_STATUSES,
  Scenario,
} from '../../api/models';
import { formatMs } from '../../shared/format';

const POLL_MS = 3000;

/**
 * Landing page: start a run (scenario, model mode, auto-approve, failure injection) and watch every persisted run.
 * Runs that are waiting on a human show which checkpoint is pending; the detail page is where the decision is made.
 */
@Component({
  selector: 'app-runs-page',
  imports: [DatePipe, FormsModule, RouterLink],
  templateUrl: './runs-page.html',
  styleUrl: './runs-page.css',
})
export class RunsPage implements OnInit {
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  readonly formatMs = formatMs;

  // Catalogue (scenarios and model modes come from the host so the form never drifts from the engine).
  readonly scenarios = signal<Scenario[]>([]);
  readonly modes = signal<string[]>([]);
  readonly catalogError = signal<string | null>(null);

  // Start form (plain fields bound with ngModel, like the approval form on the detail page).
  scenarioId = '';
  mode = 'replay';
  autoApprove = false;
  injectFailure = '';
  record = false;
  readonly starting = signal(false);
  readonly startError = signal<string | null>(null);

  // Runs list
  readonly runs = signal<RunSummary[]>([]);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly busyId = signal<string | null>(null);
  readonly notice = signal<string | null>(null);

  ngOnInit(): void {
    this.loadCatalog();
    timer(0, POLL_MS)
      .pipe(
        switchMap(() => this.fetchRuns()),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe();
  }

  // ---- Catalogue ----------------------------------------------------------

  private loadCatalog(): void {
    forkJoin({ scenarios: this.api.getScenarios(), modes: this.api.getModes() }).subscribe({
      next: ({ scenarios, modes }) => {
        this.scenarios.set(scenarios);
        this.modes.set(modes);
        this.catalogError.set(null);
        if (!this.scenarioId && scenarios.length) this.scenarioId = scenarios[0].id;
        if (!modes.includes(this.mode) && modes.length) this.mode = modes[0];
      },
      error: (err) => this.catalogError.set(ApiService.describeError(err)),
    });
  }

  selectedScenario(): Scenario | null {
    return this.scenarios().find((s) => s.id === this.scenarioId) ?? null;
  }

  // ---- Start a run --------------------------------------------------------

  start(): void {
    if (!this.scenarioId || this.starting()) return;
    this.starting.set(true);
    this.startError.set(null);
    this.api
      .startRun({
        scenario: this.scenarioId,
        mode: this.mode,
        autoApprove: this.autoApprove,
        injectFailure: this.injectFailure || null,
        record: this.record && this.mode !== 'replay',
      })
      .subscribe({
        next: (run) => {
          this.starting.set(false);
          void this.router.navigate(['/runs', run.runId]);
        },
        error: (err) => {
          this.starting.set(false);
          this.startError.set(ApiService.describeError(err));
        },
      });
  }

  // ---- Runs list ----------------------------------------------------------

  refresh(): void {
    this.fetchRuns().subscribe();
  }

  private fetchRuns(): Observable<RunSummary[] | null> {
    this.loading.set(true);
    return this.api.getRuns().pipe(
      catchError((err) => {
        this.error.set(ApiService.describeError(err));
        this.loading.set(false);
        return of(null);
      }),
      switchMap((list) => {
        if (list) {
          this.runs.set(list);
          this.error.set(null);
        }
        this.loading.set(false);
        return of(list);
      }),
    );
  }

  succeeded(run: RunSummary): number {
    return run.nodes.filter((n) => n.status === 'Succeeded').length;
  }

  canStop(run: RunSummary): boolean {
    return STOPPABLE_RUN_STATUSES.includes(run.status);
  }

  canResume(run: RunSummary): boolean {
    return RESUMABLE_RUN_STATUSES.includes(run.status);
  }

  stop(run: RunSummary): void {
    this.act(run.runId, this.api.stopRun(run.runId), `Stop requested for ${run.runId}.`);
  }

  resume(run: RunSummary): void {
    this.act(run.runId, this.api.resumeRun(run.runId), `Resume requested for ${run.runId}.`);
  }

  private act(runId: string, request: Observable<unknown>, message: string): void {
    this.busyId.set(runId);
    this.error.set(null);
    request.subscribe({
      next: () => {
        this.busyId.set(null);
        this.notice.set(message);
        setTimeout(() => this.notice.update((n) => (n === message ? null : n)), 4000);
        this.refresh();
      },
      error: (err) => {
        this.busyId.set(null);
        this.error.set(ApiService.describeError(err));
        this.refresh();
      },
    });
  }
}
