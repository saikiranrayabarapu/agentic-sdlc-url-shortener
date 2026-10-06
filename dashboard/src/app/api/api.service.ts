import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import {
  ApprovalRequest,
  AuditEvent,
  AuditVerifyResult,
  ClarificationRequest,
  MetricsAggregate,
  RunRequest,
  RunState,
  RunSummary,
  Scenario,
} from './models';

/**
 * Thin typed wrapper over the orchestrator's JSON API.
 * All URLs are relative (`/api/...`) so the same build works behind `ng serve` (proxy) and the .NET host.
 */
@Injectable({ providedIn: 'root' })
export class ApiService {
  private readonly http = inject(HttpClient);
  private readonly base = '/api';

  // ---- Catalog ------------------------------------------------------------

  getScenarios(): Observable<Scenario[]> {
    return this.http.get<Scenario[]>(`${this.base}/scenarios`);
  }

  getModes(): Observable<string[]> {
    return this.http.get<string[]>(`${this.base}/modes`);
  }

  // ---- Runs ---------------------------------------------------------------

  getRuns(): Observable<RunSummary[]> {
    return this.http.get<RunSummary[]>(`${this.base}/runs`);
  }

  startRun(request: RunRequest): Observable<RunState> {
    return this.http.post<RunState>(`${this.base}/runs`, request);
  }

  getRun(id: string): Observable<RunState> {
    return this.http.get<RunState>(`${this.base}/runs/${encodeURIComponent(id)}`);
  }

  stopRun(id: string): Observable<unknown> {
    return this.http.post(`${this.base}/runs/${encodeURIComponent(id)}/stop`, null, { responseType: 'text' });
  }

  resumeRun(id: string): Observable<RunState> {
    return this.http.post<RunState>(`${this.base}/runs/${encodeURIComponent(id)}/resume`, null);
  }

  // ---- Human checkpoints --------------------------------------------------

  decideApproval(runId: string, approvalId: string, body: ApprovalRequest): Observable<unknown> {
    return this.http.post(
      `${this.base}/runs/${encodeURIComponent(runId)}/approvals/${encodeURIComponent(approvalId)}`,
      body,
      { responseType: 'text' },
    );
  }

  answerClarification(runId: string, clarificationId: string, body: ClarificationRequest): Observable<unknown> {
    return this.http.post(
      `${this.base}/runs/${encodeURIComponent(runId)}/clarifications/${encodeURIComponent(clarificationId)}`,
      body,
      { responseType: 'text' },
    );
  }

  // ---- Audit --------------------------------------------------------------

  getAudit(runId: string, afterSeq?: number): Observable<AuditEvent[]> {
    let params = new HttpParams();
    if (afterSeq !== undefined) params = params.set('after', String(afterSeq));
    return this.http.get<AuditEvent[]>(`${this.base}/runs/${encodeURIComponent(runId)}/audit`, { params });
  }

  verifyAudit(runId: string): Observable<AuditVerifyResult> {
    return this.http.get<AuditVerifyResult>(`${this.base}/runs/${encodeURIComponent(runId)}/audit/verify`);
  }

  // ---- Workspace ----------------------------------------------------------

  getWorkspaceFiles(runId: string): Observable<string[]> {
    return this.http.get<string[]>(`${this.base}/runs/${encodeURIComponent(runId)}/workspace`);
  }

  getWorkspaceFile(runId: string, path: string): Observable<string> {
    const params = new HttpParams().set('path', path);
    return this.http.get(`${this.base}/runs/${encodeURIComponent(runId)}/workspace/file`, {
      params,
      responseType: 'text',
    });
  }

  // ---- Metrics ------------------------------------------------------------

  getMetrics(): Observable<MetricsAggregate> {
    return this.http.get<MetricsAggregate>(`${this.base}/metrics`);
  }

  /** Extracts a readable message from an HTTP error (the host returns `{ error }` bodies on 400/409). */
  static describeError(err: unknown): string {
    if (err instanceof HttpErrorResponse) {
      const body = err.error as { error?: string } | string | null;
      if (body && typeof body === 'object' && typeof body.error === 'string') return body.error;
      if (typeof body === 'string' && body.trim()) {
        try {
          const parsed = JSON.parse(body) as { error?: string };
          if (parsed && typeof parsed.error === 'string') return parsed.error;
        } catch {
          return body;
        }
      }
      return err.status ? `${err.status} ${err.statusText}` : 'Network error: API unreachable';
    }
    return err instanceof Error ? err.message : String(err);
  }
}
