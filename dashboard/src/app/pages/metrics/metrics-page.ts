import { Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { PercentPipe } from '@angular/common';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { catchError, of, switchMap, timer } from 'rxjs';
import { ApiService } from '../../api/api.service';
import { MetricsAggregate, ScenarioMetrics } from '../../api/models';
import { formatMs } from '../../shared/format';

const POLL_MS = 5000;

@Component({
  selector: 'app-metrics-page',
  imports: [PercentPipe],
  templateUrl: './metrics-page.html',
  styleUrl: './metrics-page.css',
})
export class MetricsPage implements OnInit {
  private readonly api = inject(ApiService);
  private readonly destroyRef = inject(DestroyRef);

  readonly formatMs = formatMs;
  readonly metrics = signal<MetricsAggregate | null>(null);
  readonly error = signal<string | null>(null);
  readonly loading = signal(false);

  readonly byStatus = computed<[string, number][]>(() =>
    Object.entries(this.metrics()?.byStatus ?? {}).sort(([, a], [, b]) => b - a),
  );
  readonly byScenario = computed<[string, ScenarioMetrics][]>(() =>
    Object.entries(this.metrics()?.byScenario ?? {}).sort(([a], [b]) => a.localeCompare(b)),
  );

  ngOnInit(): void {
    timer(0, POLL_MS)
      .pipe(
        switchMap(() => this.fetch()),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe();
  }

  refresh(): void {
    this.fetch().subscribe();
  }

  private fetch() {
    this.loading.set(true);
    return this.api.getMetrics().pipe(
      catchError((err) => {
        this.error.set(ApiService.describeError(err));
        this.loading.set(false);
        return of(null);
      }),
      switchMap((m) => {
        if (m) {
          this.metrics.set(m);
          this.error.set(null);
        }
        this.loading.set(false);
        return of(m);
      }),
    );
  }

  rate(completed: number, runs: number): number | null {
    return runs ? completed / runs : null;
  }
}
