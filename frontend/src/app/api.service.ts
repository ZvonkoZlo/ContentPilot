import { HttpClient, HttpHeaders } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { SettingsService } from './settings.service';
import {
  Brand,
  Campaign,
  CampaignCost,
  CampaignPackage,
  ContentItemSummary,
  ItemDetail,
  QaPassRate,
  Tenant,
} from './models';

/**
 * A thin wrapper over HttpClient, not a real API client generator — every method here maps
 * one-to-one to an endpoint in CampaignEndpoints.cs/BrandEndpoints.cs/TenantEndpoints.cs.
 * X-Tenant-Id is attached per call from whatever the settings bar currently holds, since
 * there is no session to carry it for us.
 */
@Injectable({ providedIn: 'root' })
export class ApiService {
  constructor(private readonly http: HttpClient, private readonly settings: SettingsService) {}

  private get base(): string {
    return this.settings.settings().apiBaseUrl.replace(/\/+$/, '');
  }

  private get headers(): HttpHeaders {
    const tenantId = this.settings.settings().tenantId;
    return tenantId ? new HttpHeaders({ 'X-Tenant-Id': tenantId }) : new HttpHeaders();
  }

  listTenants(): Observable<Tenant[]> {
    return this.http.get<Tenant[]>(`${this.base}/api/tenants`);
  }

  listBrands(): Observable<Brand[]> {
    return this.http.get<Brand[]>(`${this.base}/api/brands`, { headers: this.headers });
  }

  listCampaigns(brandId?: string): Observable<Campaign[]> {
    const url = brandId
      ? `${this.base}/api/campaigns?brandId=${encodeURIComponent(brandId)}`
      : `${this.base}/api/campaigns`;
    return this.http.get<Campaign[]>(url, { headers: this.headers });
  }

  getCampaign(id: string): Observable<Campaign> {
    return this.http.get<Campaign>(`${this.base}/api/campaigns/${id}`, { headers: this.headers });
  }

  triggerCampaign(brandId: string, weekStart?: string): Observable<Campaign> {
    return this.http.post<Campaign>(
      `${this.base}/api/campaigns`,
      { brandId, weekStart: weekStart || null },
      { headers: this.headers }
    );
  }

  cancelCampaign(id: string): Observable<Campaign> {
    return this.http.post<Campaign>(`${this.base}/api/campaigns/${id}/cancel`, null, { headers: this.headers });
  }

  listItems(campaignId: string): Observable<ContentItemSummary[]> {
    return this.http.get<ContentItemSummary[]>(`${this.base}/api/campaigns/${campaignId}/items`, {
      headers: this.headers,
    });
  }

  getItem(campaignId: string, itemId: string): Observable<ItemDetail> {
    return this.http.get<ItemDetail>(`${this.base}/api/campaigns/${campaignId}/items/${itemId}`, {
      headers: this.headers,
    });
  }

  approveItem(campaignId: string, itemId: string): Observable<ContentItemSummary> {
    return this.http.post<ContentItemSummary>(
      `${this.base}/api/campaigns/${campaignId}/items/${itemId}/approve`,
      null,
      { headers: this.headers }
    );
  }

  rejectItem(campaignId: string, itemId: string, reason: string): Observable<ContentItemSummary> {
    return this.http.post<ContentItemSummary>(
      `${this.base}/api/campaigns/${campaignId}/items/${itemId}/reject`,
      { reason },
      { headers: this.headers }
    );
  }

  rateItem(campaignId: string, itemId: string, score: number, note?: string): Observable<unknown> {
    return this.http.post(
      `${this.base}/api/campaigns/${campaignId}/items/${itemId}/rating`,
      { score, note: note || null },
      { headers: this.headers }
    );
  }

  getPackage(campaignId: string): Observable<CampaignPackage> {
    return this.http.get<CampaignPackage>(`${this.base}/api/campaigns/${campaignId}/package`, {
      headers: this.headers,
    });
  }

  download(campaignId: string): Observable<{ url: string }> {
    return this.http.post<{ url: string }>(
      `${this.base}/api/campaigns/${campaignId}/download`,
      null,
      { headers: this.headers }
    );
  }

  getCost(campaignId: string): Observable<CampaignCost> {
    return this.http.get<CampaignCost>(`${this.base}/api/campaigns/${campaignId}/cost`, {
      headers: this.headers,
    });
  }

  getQaPassRate(campaignId: string): Observable<QaPassRate> {
    return this.http.get<QaPassRate>(`${this.base}/api/campaigns/${campaignId}/qa-pass-rate`, {
      headers: this.headers,
    });
  }
}
