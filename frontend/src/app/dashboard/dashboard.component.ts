import { Component, computed, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { ApiService } from '../api.service';
import { SettingsService } from '../settings.service';
import { Campaign } from '../models';

@Component({
  selector: 'app-dashboard',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink],
  templateUrl: './dashboard.component.html',
  styleUrl: './dashboard.component.css',
})
export class DashboardComponent {
  readonly campaigns = signal<Campaign[]>([]);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly triggering = signal(false);

  readonly brandId = computed(() => this.settings.settings().brandId);
  readonly ready = computed(() => !!this.settings.settings().tenantId && !!this.brandId());

  weekStart = '';

  constructor(private readonly api: ApiService, private readonly settings: SettingsService) {}

  ngOnInit(): void {
    this.refresh();
  }

  refresh(): void {
    if (!this.settings.settings().tenantId) {
      return;
    }

    this.loading.set(true);
    this.error.set(null);

    this.api.listCampaigns(this.brandId() || undefined).subscribe({
      next: (campaigns) => {
        this.campaigns.set(campaigns);
        this.loading.set(false);
      },
      error: () => {
        this.error.set('Could not list campaigns. Check the tenant is selected and the API is reachable.');
        this.loading.set(false);
      },
    });
  }

  trigger(): void {
    const brandId = this.brandId();
    if (!brandId) {
      return;
    }

    this.triggering.set(true);
    this.error.set(null);

    this.api.triggerCampaign(brandId, this.weekStart || undefined).subscribe({
      next: () => {
        this.triggering.set(false);
        this.weekStart = '';
        this.refresh();
      },
      error: (err) => {
        this.triggering.set(false);
        const detail = err?.error?.detail ?? 'Could not trigger the campaign.';
        this.error.set(detail);
      },
    });
  }

  statusClass(status: string): string {
    if (status === 'Ready') return 'ok';
    if (['Failed', 'Cancelled'].includes(status)) return 'bad';
    if (status === 'PartiallyReady') return 'warn';
    return '';
  }
}
