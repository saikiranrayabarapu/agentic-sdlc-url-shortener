import { Routes } from '@angular/router';
import { RunsPage } from './pages/runs/runs-page';
import { RunDetailPage } from './pages/run-detail/run-detail-page';
import { MetricsPage } from './pages/metrics/metrics-page';

export const routes: Routes = [
  { path: '', component: RunsPage, title: 'Runs · Agentic SDLC' },
  { path: 'runs/:id', component: RunDetailPage, title: 'Run · Agentic SDLC' },
  { path: 'metrics', component: MetricsPage, title: 'Metrics · Agentic SDLC' },
  { path: '**', redirectTo: '' },
];
