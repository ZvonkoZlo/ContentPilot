import { Component, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { CommonModule } from '@angular/common';
import { ApiService } from '../api.service';
import { SettingsService } from '../settings.service';
import { Brand, Tenant } from '../models';

/**
 * Stands in for a login screen: pick the API URL, the tenant, and the brand once, and every
 * page below reads them from SettingsService. Nothing here is a credential — X-Tenant-Id is
 * a plain header per CLAUDE.md's stated MVP scope, not an auth boundary.
 */
@Component({
  selector: 'app-settings-bar',
  standalone: true,
  imports: [FormsModule, CommonModule],
  templateUrl: './settings-bar.component.html',
  styleUrl: './settings-bar.component.css',
})
export class SettingsBarComponent implements OnInit {
  readonly settings = this.settingsService.settings;
  readonly tenants = signal<Tenant[]>([]);
  readonly brands = signal<Brand[]>([]);
  readonly error = signal<string | null>(null);

  apiBaseUrl = '';
  tenantId = '';
  brandId = '';

  constructor(private readonly api: ApiService, private readonly settingsService: SettingsService) {}

  ngOnInit(): void {
    const current = this.settings();
    this.apiBaseUrl = current.apiBaseUrl;
    this.tenantId = current.tenantId;
    this.brandId = current.brandId;

    this.refreshTenants();
    if (this.tenantId) {
      this.refreshBrands();
    }
  }

  applyApiUrl(): void {
    this.settingsService.update({ apiBaseUrl: this.apiBaseUrl });
    this.refreshTenants();
  }

  refreshTenants(): void {
    this.error.set(null);
    this.api.listTenants().subscribe({
      next: (tenants) => this.tenants.set(tenants),
      error: () => this.error.set('Could not reach the API at this URL — is it running?'),
    });
  }

  onTenantChange(): void {
    this.settingsService.update({ tenantId: this.tenantId, brandId: '' });
    this.brandId = '';
    this.brands.set([]);
    if (this.tenantId) {
      this.refreshBrands();
    }
  }

  refreshBrands(): void {
    this.api.listBrands().subscribe({
      next: (brands) => this.brands.set(brands),
      error: () => this.error.set('Could not list brands for this tenant.'),
    });
  }

  onBrandChange(): void {
    this.settingsService.update({ brandId: this.brandId });
  }
}
