import { Component, computed, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ApiService } from '../api.service';
import { SettingsService } from '../settings.service';
import { AssetKind, BrandAsset, BrandProfile } from '../models';

const EMPTY_PROFILE: BrandProfile = {
  visual: {
    primaryColor: '#3B5BDB',
    secondaryColor: null,
    accentColor: null,
    darkColor: '#12131A',
    lightColor: '#F7F8FA',
    headingFont: 'Archivo',
    bodyFont: 'Inter',
    cornerRadius: 16,
    styleKeywords: [],
  },
  voice: {
    summary: '',
    traits: [],
    avoid: [],
    preferredCtaStyle: null,
    bannedWords: [],
    forbiddenClaims: [],
  },
  messaging: {
    positioning: '',
    principles: [],
    corePromise: null,
  },
  operatorNotes: null,
};

/** Comma-separated text in the UI maps to string[] on the wire — kept deliberately simple. */
function toList(text: string): string[] {
  return text
    .split(',')
    .map((s) => s.trim())
    .filter((s) => s.length > 0);
}

@Component({
  selector: 'app-brand-profile',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './brand-profile.component.html',
  styleUrl: './brand-profile.component.css',
})
export class BrandProfileComponent {
  readonly brandId = computed(() => this.settings.settings().brandId);
  readonly ready = computed(() => !!this.settings.settings().tenantId && !!this.brandId());

  readonly profile = signal<BrandProfile>(structuredClone(EMPTY_PROFILE));
  readonly profileFound = signal(false);
  readonly loading = signal(false);
  readonly saving = signal(false);
  readonly error = signal<string | null>(null);
  readonly saved = signal(false);

  // Text-form scratch fields for the array properties (comma-separated).
  styleKeywordsText = '';
  traitsText = '';
  avoidText = '';
  bannedWordsText = '';
  forbiddenClaimsText = '';
  principlesText = '';

  readonly assets = signal<BrandAsset[]>([]);
  readonly assetsLoading = signal(false);
  readonly assetsError = signal<string | null>(null);
  readonly uploading = signal(false);
  readonly includeArchived = signal(false);

  uploadKind: AssetKind = 'ProductScreenshot';
  uploadDescription = '';
  uploadTags = '';
  selectedFile: File | null = null;

  constructor(private readonly api: ApiService, private readonly settings: SettingsService) {}

  ngOnInit(): void {
    this.refresh();
  }

  refresh(): void {
    const brandId = this.brandId();
    if (!brandId) {
      return;
    }

    this.loading.set(true);
    this.error.set(null);

    this.api.getBrandProfile(brandId).subscribe({
      next: (profile) => {
        this.profile.set(profile);
        this.profileFound.set(true);
        this.syncTextFields();
        this.loading.set(false);
      },
      error: (err) => {
        if (err.status === 404) {
          this.profile.set(structuredClone(EMPTY_PROFILE));
          this.profileFound.set(false);
          this.syncTextFields();
        } else {
          this.error.set('Could not load the brand profile.');
        }
        this.loading.set(false);
      },
    });

    this.refreshAssets();
  }

  refreshAssets(): void {
    const brandId = this.brandId();
    if (!brandId) {
      return;
    }

    this.assetsLoading.set(true);
    this.assetsError.set(null);

    this.api.listAssets(brandId, this.includeArchived()).subscribe({
      next: (assets) => {
        this.assets.set(assets);
        this.assetsLoading.set(false);
      },
      error: () => {
        this.assetsError.set('Could not list assets.');
        this.assetsLoading.set(false);
      },
    });
  }

  private syncTextFields(): void {
    const p = this.profile();
    this.styleKeywordsText = p.visual.styleKeywords.join(', ');
    this.traitsText = p.voice.traits.join(', ');
    this.avoidText = p.voice.avoid.join(', ');
    this.bannedWordsText = p.voice.bannedWords.join(', ');
    this.forbiddenClaimsText = p.voice.forbiddenClaims.join(', ');
    this.principlesText = p.messaging.principles.join(', ');
  }

  save(): void {
    const brandId = this.brandId();
    if (!brandId) {
      return;
    }

    const current = this.profile();
    const toSave: BrandProfile = {
      ...current,
      visual: { ...current.visual, styleKeywords: toList(this.styleKeywordsText) },
      voice: {
        ...current.voice,
        traits: toList(this.traitsText),
        avoid: toList(this.avoidText),
        bannedWords: toList(this.bannedWordsText),
        forbiddenClaims: toList(this.forbiddenClaimsText),
      },
      messaging: { ...current.messaging, principles: toList(this.principlesText) },
    };

    this.saving.set(true);
    this.error.set(null);
    this.saved.set(false);

    this.api.saveBrandProfile(brandId, toSave).subscribe({
      next: (profile) => {
        this.profile.set(profile);
        this.profileFound.set(true);
        this.syncTextFields();
        this.saving.set(false);
        this.saved.set(true);
        setTimeout(() => this.saved.set(false), 2500);
      },
      error: (err) => {
        this.saving.set(false);
        this.error.set(err?.error?.detail ?? 'Could not save the brand profile.');
      },
    });
  }

  onFileSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    this.selectedFile = input.files && input.files.length > 0 ? input.files[0] : null;
  }

  upload(): void {
    const brandId = this.brandId();
    if (!brandId || !this.selectedFile) {
      return;
    }

    this.uploading.set(true);
    this.assetsError.set(null);

    this.api.uploadAsset(brandId, this.selectedFile, this.uploadKind, this.uploadDescription, this.uploadTags).subscribe({
      next: () => {
        this.uploading.set(false);
        this.selectedFile = null;
        this.uploadDescription = '';
        this.uploadTags = '';
        this.refreshAssets();
      },
      error: (err) => {
        this.uploading.set(false);
        this.assetsError.set(err?.error?.detail ?? 'Upload failed.');
      },
    });
  }

  archive(asset: BrandAsset): void {
    const brandId = this.brandId();
    if (!brandId) {
      return;
    }

    this.api.archiveAsset(brandId, asset.id).subscribe({
      next: () => this.refreshAssets(),
      error: () => this.assetsError.set('Could not archive the asset.'),
    });
  }

  openAsset(asset: BrandAsset): void {
    const brandId = this.brandId();
    if (!brandId) {
      return;
    }

    this.api.getAssetUrl(brandId, asset.id).subscribe({
      next: ({ url }) => window.open(url, '_blank'),
      error: () => this.assetsError.set('Could not mint a download URL.'),
    });
  }

  formatBytes(bytes: number): string {
    return `${(bytes / 1024).toFixed(0)} KB`;
  }
}
