import { HttpClient, HttpHeaders } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { SettingsService } from './settings.service';
import {
  AssetKind,
  AudiencePersona,
  Brand,
  BrandAsset,
  BrandProfile,
  Campaign,
  CampaignCost,
  CampaignPackage,
  ContentPreferences,
  ContentItemSummary,
  ItemDetail,
  ItemImage,
  FactCategory,
  PersonaDetail,
  ProductFact,
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

  getItemImage(campaignId: string, itemId: string): Observable<ItemImage> {
    return this.http.get<ItemImage>(`${this.base}/api/campaigns/${campaignId}/items/${itemId}/image`, {
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

  getBrandProfile(brandId: string): Observable<BrandProfile> {
    return this.http.get<BrandProfile>(`${this.base}/api/brands/${brandId}/profile`, { headers: this.headers });
  }

  listFacts(brandId: string): Observable<ProductFact[]> {
    return this.http.get<ProductFact[]>(`${this.base}/api/brands/${brandId}/facts`, { headers: this.headers });
  }

  addFact(
    brandId: string,
    fact: {
      key: string;
      statement: string;
      category: FactCategory;
      evidence: string | null;
      isPublic: boolean;
      validFrom: string | null;
      validTo: string | null;
    }
  ): Observable<ProductFact> {
    return this.http.post<ProductFact>(`${this.base}/api/brands/${brandId}/facts`, fact, {
      headers: this.headers,
    });
  }

  deleteFact(brandId: string, factId: string): Observable<void> {
    return this.http.delete<void>(`${this.base}/api/brands/${brandId}/facts/${factId}`, {
      headers: this.headers,
    });
  }

  listPersonas(brandId: string): Observable<AudiencePersona[]> {
    return this.http.get<AudiencePersona[]>(`${this.base}/api/brands/${brandId}/personas`, {
      headers: this.headers,
    });
  }

  addPersona(
    brandId: string,
    persona: { name: string; segment: string; detail: PersonaDetail; isPrimary: boolean }
  ): Observable<AudiencePersona> {
    return this.http.post<AudiencePersona>(`${this.base}/api/brands/${brandId}/personas`, persona, {
      headers: this.headers,
    });
  }

  getPreferences(brandId: string): Observable<ContentPreferences> {
    return this.http.get<ContentPreferences>(`${this.base}/api/brands/${brandId}/preferences`, {
      headers: this.headers,
    });
  }

  savePreferences(brandId: string, preferences: Omit<ContentPreferences, 'totalPerWeek'>): Observable<ContentPreferences> {
    return this.http.put<ContentPreferences>(`${this.base}/api/brands/${brandId}/preferences`, preferences, {
      headers: this.headers,
    });
  }

  saveBrandProfile(brandId: string, profile: BrandProfile): Observable<BrandProfile> {
    return this.http.put<BrandProfile>(`${this.base}/api/brands/${brandId}/profile`, profile, {
      headers: this.headers,
    });
  }

  listAssets(brandId: string, includeArchived = false): Observable<BrandAsset[]> {
    return this.http.get<BrandAsset[]>(
      `${this.base}/api/brands/${brandId}/assets?includeArchived=${includeArchived}`,
      { headers: this.headers }
    );
  }

  uploadAsset(
    brandId: string,
    file: File,
    kind: AssetKind,
    description?: string,
    tags?: string
  ): Observable<BrandAsset> {
    const form = new FormData();
    form.append('file', file);
    form.append('kind', kind);
    if (description) form.append('description', description);
    if (tags) form.append('tags', tags);
    return this.http.post<BrandAsset>(`${this.base}/api/brands/${brandId}/assets`, form, {
      headers: this.headers,
    });
  }

  archiveAsset(brandId: string, assetId: string): Observable<BrandAsset> {
    return this.http.post<BrandAsset>(
      `${this.base}/api/brands/${brandId}/assets/${assetId}/archive`,
      null,
      { headers: this.headers }
    );
  }

  getAssetUrl(brandId: string, assetId: string): Observable<{ url: string }> {
    return this.http.get<{ url: string }>(`${this.base}/api/brands/${brandId}/assets/${assetId}/url`, {
      headers: this.headers,
    });
  }
}
