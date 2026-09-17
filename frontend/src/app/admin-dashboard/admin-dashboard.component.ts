import { CommonModule } from '@angular/common';
import { Component, signal } from '@angular/core';
import { forkJoin } from 'rxjs';
import { ApiService } from '../api.service';
import { DeadJob, EvalRun, StuckRun } from '../models';

@Component({
  selector: 'app-admin-dashboard',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './admin-dashboard.component.html',
  styleUrl: './admin-dashboard.component.css',
})
export class AdminDashboardComponent {
  readonly deadJobs = signal<DeadJob[]>([]);
  readonly stuckRuns = signal<StuckRun[]>([]);
  readonly evalRuns = signal<EvalRun[]>([]);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);

  constructor(private readonly api: ApiService) {}

  ngOnInit(): void {
    this.refresh();
  }

  refresh(): void {
    this.loading.set(true);
    this.error.set(null);
    forkJoin({
      deadJobs: this.api.listDeadJobs(),
      stuckRuns: this.api.listStuckRuns(),
      evalRuns: this.api.listEvalRuns(),
    }).subscribe({
      next: ({ deadJobs, stuckRuns, evalRuns }) => {
        this.deadJobs.set(deadJobs);
        this.stuckRuns.set(stuckRuns);
        this.evalRuns.set(evalRuns);
        this.loading.set(false);
      },
      error: () => {
        this.error.set('Could not load operator data. Check that the API is reachable.');
        this.loading.set(false);
      },
    });
  }
}
