import { Component, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { ApiService } from '../api.service';
import { Campaign, CampaignCost, ContentItemSummary, ItemDetail, QaPassRate } from '../models';

@Component({
  selector: 'app-campaign-detail',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink],
  templateUrl: './campaign-detail.component.html',
  styleUrl: './campaign-detail.component.css',
})
export class CampaignDetailComponent {
  campaignId = '';

  readonly campaign = signal<Campaign | null>(null);
  readonly items = signal<ContentItemSummary[]>([]);
  readonly cost = signal<CampaignCost | null>(null);
  readonly qaPassRate = signal<QaPassRate | null>(null);
  readonly selectedItem = signal<ItemDetail | null>(null);
  readonly error = signal<string | null>(null);
  readonly downloadUrl = signal<string | null>(null);
  readonly busy = signal(false);

  rejectReason = '';
  rateScore = 5;
  rateNote = '';

  constructor(private readonly route: ActivatedRoute, private readonly api: ApiService) {}

  ngOnInit(): void {
    this.campaignId = this.route.snapshot.paramMap.get('id') ?? '';
    this.refresh();
  }

  refresh(): void {
    this.error.set(null);

    this.api.getCampaign(this.campaignId).subscribe({
      next: (c) => this.campaign.set(c),
      error: () => this.error.set('Could not load this campaign — check the tenant selected above matches.'),
    });

    this.api.listItems(this.campaignId).subscribe({ next: (items) => this.items.set(items) });
    this.api.getCost(this.campaignId).subscribe({ next: (cost) => this.cost.set(cost) });
    this.api.getQaPassRate(this.campaignId).subscribe({ next: (rate) => this.qaPassRate.set(rate) });
  }

  openItem(itemId: string): void {
    this.api.getItem(this.campaignId, itemId).subscribe({
      next: (detail) => this.selectedItem.set(detail),
      error: () => this.error.set('Could not load item detail.'),
    });
  }

  closeItem(): void {
    this.selectedItem.set(null);
    this.rejectReason = '';
  }

  approve(itemId: string): void {
    this.busy.set(true);
    this.api.approveItem(this.campaignId, itemId).subscribe({
      next: () => {
        this.busy.set(false);
        this.closeItem();
        this.refresh();
      },
      error: (err) => {
        this.busy.set(false);
        this.error.set(err?.error?.detail ?? 'Could not approve this item.');
      },
    });
  }

  reject(itemId: string): void {
    this.busy.set(true);
    this.api.rejectItem(this.campaignId, itemId, this.rejectReason).subscribe({
      next: () => {
        this.busy.set(false);
        this.closeItem();
        this.refresh();
      },
      error: (err) => {
        this.busy.set(false);
        this.error.set(err?.error?.detail ?? 'Could not reject this item.');
      },
    });
  }

  rate(itemId: string): void {
    this.busy.set(true);
    this.api.rateItem(this.campaignId, itemId, this.rateScore, this.rateNote).subscribe({
      next: () => {
        this.busy.set(false);
      },
      error: (err) => {
        this.busy.set(false);
        this.error.set(err?.error?.detail ?? 'Could not save the rating.');
      },
    });
  }

  cancelCampaign(): void {
    this.api.cancelCampaign(this.campaignId).subscribe({
      next: (c) => this.campaign.set(c),
      error: (err) => this.error.set(err?.error?.detail ?? 'Could not cancel this campaign.'),
    });
  }

  download(): void {
    this.downloadUrl.set(null);
    this.busy.set(true);
    this.api.download(this.campaignId).subscribe({
      next: (result) => {
        this.busy.set(false);
        this.downloadUrl.set(result.url);
      },
      error: (err) => {
        this.busy.set(false);
        this.error.set(err?.error?.detail ?? 'Could not build or fetch the download.');
      },
    });
  }

  formatCents(microCents: number): string {
    return `$${(microCents / 100_000_000).toFixed(4)}`;
  }

  statusClass(status: string): string {
    if (['Approved', 'Ready'].includes(status)) return 'ok';
    if (['Failed', 'Cancelled'].includes(status)) return 'bad';
    if (status === 'NeedsHumanReview') return 'warn';
    return '';
  }
}
