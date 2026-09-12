import { Injectable, signal } from '@angular/core';

const STORAGE_KEY = 'contentpilot.settings';

export interface Settings {
  apiBaseUrl: string;
  tenantId: string;
  brandId: string;
}

const DEFAULTS: Settings = { apiBaseUrl: 'http://localhost:8080', tenantId: '', brandId: '' };

/**
 * Everything this UI needs to talk to the API and nothing this UI needs a login for: the
 * base URL, and the tenant/brand a curl-driven backend would otherwise need pasted into
 * every request. There is no auth story yet (the API's own MVP scope, per CLAUDE.md), so a
 * plain settings bar the tester fills in once is the whole "login" this needs.
 */
@Injectable({ providedIn: 'root' })
export class SettingsService {
  private readonly state = signal<Settings>(this.load());

  readonly settings = this.state.asReadonly();

  update(patch: Partial<Settings>): void {
    const next = { ...this.state(), ...patch };
    this.state.set(next);
    localStorage.setItem(STORAGE_KEY, JSON.stringify(next));
  }

  private load(): Settings {
    try {
      const raw = localStorage.getItem(STORAGE_KEY);
      return raw ? { ...DEFAULTS, ...JSON.parse(raw) } : DEFAULTS;
    } catch {
      return DEFAULTS;
    }
  }
}
