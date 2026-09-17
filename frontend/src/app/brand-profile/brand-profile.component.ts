import { Component, computed, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ApiService } from '../api.service';
import { SettingsService } from '../settings.service';
import {
  AssetKind,
  AudiencePersona,
  BrandAsset,
  BrandProfile,
  ContentPreferences,
  FactCategory,
  ProductFact,
  WeekDay,
} from '../models';

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

const DEFAULT_PREFERENCES: ContentPreferences = {
  postsPerWeek: 2,
  carouselsPerWeek: 1,
  reelsPerWeek: 1,
  totalPerWeek: 4,
  excludedTopics: [],
  preferredTopics: [],
  publishDays: ['Tuesday', 'Thursday'],
  generationDay: 'Monday',
  generationTime: '06:00',
  scheduledGenerationEnabled: true,
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

  readonly facts = signal<ProductFact[]>([]);
  readonly factsLoading = signal(false);
  readonly factsError = signal<string | null>(null);
  readonly addingFact = signal(false);

  factKey = '';
  factStatement = '';
  factCategory: FactCategory = 'Feature';
  factEvidence = '';
  factIsPublic = true;
  factValidFrom = '';
  factValidTo = '';

  readonly personas = signal<AudiencePersona[]>([]);
  readonly personasLoading = signal(false);
  readonly personasError = signal<string | null>(null);
  readonly addingPersona = signal(false);

  personaName = '';
  personaSegment = '';
  personaPains = '';
  personaGoals = '';
  personaObjections = '';
  personaVocabulary = '';
  personaContext = '';
  personaIsPrimary = false;

  readonly preferences = signal<ContentPreferences>(structuredClone(DEFAULT_PREFERENCES));
  readonly preferencesFound = signal(false);
  readonly preferencesLoading = signal(false);
  readonly preferencesSaving = signal(false);
  readonly preferencesSaved = signal(false);
  readonly preferencesError = signal<string | null>(null);
  readonly weekDays: WeekDay[] = [
    'Monday',
    'Tuesday',
    'Wednesday',
    'Thursday',
    'Friday',
    'Saturday',
    'Sunday',
  ];
  publishDaySelection: Record<WeekDay, boolean> = this.emptyDaySelection();
  excludedTopicsText = '';
  preferredTopicsText = '';

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
    this.refreshFacts();
    this.refreshPersonas();
    this.refreshPreferences();
  }

  refreshFacts(): void {
    const brandId = this.brandId();
    if (!brandId) return;

    this.factsLoading.set(true);
    this.factsError.set(null);
    this.api.listFacts(brandId).subscribe({
      next: (facts) => {
        this.facts.set(facts);
        this.factsLoading.set(false);
      },
      error: () => {
        this.factsError.set('Could not load product facts.');
        this.factsLoading.set(false);
      },
    });
  }

  addFact(): void {
    const brandId = this.brandId();
    if (!brandId || !this.factKey.trim() || !this.factStatement.trim()) return;

    this.addingFact.set(true);
    this.factsError.set(null);
    this.api
      .addFact(brandId, {
        key: this.factKey.trim(),
        statement: this.factStatement.trim(),
        category: this.factCategory,
        evidence: this.factEvidence.trim() || null,
        isPublic: this.factIsPublic,
        validFrom: this.toApiDate(this.factValidFrom),
        validTo: this.toApiDate(this.factValidTo),
      })
      .subscribe({
        next: () => {
          this.addingFact.set(false);
          this.factKey = '';
          this.factStatement = '';
          this.factEvidence = '';
          this.factValidFrom = '';
          this.factValidTo = '';
          this.refreshFacts();
        },
        error: (err) => {
          this.addingFact.set(false);
          this.factsError.set(err?.error?.detail ?? 'Could not add the product fact.');
        },
      });
  }

  deleteFact(fact: ProductFact): void {
    const brandId = this.brandId();
    if (!brandId) return;

    this.factsError.set(null);
    this.api.deleteFact(brandId, fact.id).subscribe({
      next: () => this.refreshFacts(),
      error: () => this.factsError.set('Could not delete the product fact.'),
    });
  }

  refreshPersonas(): void {
    const brandId = this.brandId();
    if (!brandId) return;

    this.personasLoading.set(true);
    this.personasError.set(null);
    this.api.listPersonas(brandId).subscribe({
      next: (personas) => {
        this.personas.set(personas);
        this.personasLoading.set(false);
      },
      error: () => {
        this.personasError.set('Could not load audience personas.');
        this.personasLoading.set(false);
      },
    });
  }

  addPersona(): void {
    const brandId = this.brandId();
    if (!brandId || !this.personaName.trim() || !this.personaSegment.trim()) return;

    this.addingPersona.set(true);
    this.personasError.set(null);
    this.api
      .addPersona(brandId, {
        name: this.personaName.trim(),
        segment: this.personaSegment.trim(),
        isPrimary: this.personaIsPrimary,
        detail: {
          pains: toList(this.personaPains),
          goals: toList(this.personaGoals),
          objections: toList(this.personaObjections),
          vocabulary: toList(this.personaVocabulary),
          context: this.personaContext.trim() || null,
        },
      })
      .subscribe({
        next: () => {
          this.addingPersona.set(false);
          this.personaName = '';
          this.personaSegment = '';
          this.personaPains = '';
          this.personaGoals = '';
          this.personaObjections = '';
          this.personaVocabulary = '';
          this.personaContext = '';
          this.personaIsPrimary = false;
          this.refreshPersonas();
        },
        error: (err) => {
          this.addingPersona.set(false);
          this.personasError.set(err?.error?.detail ?? 'Could not add the audience persona.');
        },
      });
  }

  refreshPreferences(): void {
    const brandId = this.brandId();
    if (!brandId) return;

    this.preferencesLoading.set(true);
    this.preferencesError.set(null);
    this.api.getPreferences(brandId).subscribe({
      next: (preferences) => {
        this.preferences.set(preferences);
        this.preferencesFound.set(true);
        this.syncPreferenceFields();
        this.preferencesLoading.set(false);
      },
      error: (err) => {
        if (err.status === 404) {
          this.preferences.set(structuredClone(DEFAULT_PREFERENCES));
          this.preferencesFound.set(false);
          this.syncPreferenceFields();
        } else {
          this.preferencesError.set('Could not load content preferences.');
        }
        this.preferencesLoading.set(false);
      },
    });
  }

  savePreferences(): void {
    const brandId = this.brandId();
    if (!brandId) return;

    const publishDays = this.weekDays.filter((day) => this.publishDaySelection[day]);
    if (publishDays.length === 0) {
      this.preferencesError.set('Choose at least one publish day.');
      return;
    }

    const current = this.preferences();
    this.preferencesSaving.set(true);
    this.preferencesSaved.set(false);
    this.preferencesError.set(null);
    this.api
      .savePreferences(brandId, {
        postsPerWeek: current.postsPerWeek,
        carouselsPerWeek: current.carouselsPerWeek,
        reelsPerWeek: current.reelsPerWeek,
        excludedTopics: toList(this.excludedTopicsText),
        preferredTopics: toList(this.preferredTopicsText),
        publishDays,
        generationDay: current.generationDay,
        generationTime: current.generationTime,
        scheduledGenerationEnabled: current.scheduledGenerationEnabled,
      })
      .subscribe({
        next: (preferences) => {
          this.preferences.set(preferences);
          this.preferencesFound.set(true);
          this.syncPreferenceFields();
          this.preferencesSaving.set(false);
          this.preferencesSaved.set(true);
          setTimeout(() => this.preferencesSaved.set(false), 2500);
        },
        error: (err) => {
          this.preferencesSaving.set(false);
          this.preferencesError.set(err?.error?.detail ?? 'Could not save content preferences.');
        },
      });
  }

  private syncPreferenceFields(): void {
    const preferences = this.preferences();
    this.excludedTopicsText = preferences.excludedTopics.join(', ');
    this.preferredTopicsText = preferences.preferredTopics.join(', ');
    this.publishDaySelection = this.emptyDaySelection();
    for (const day of preferences.publishDays) this.publishDaySelection[day] = true;
    if (preferences.generationTime.length > 5) {
      this.preferences.update((current) => ({ ...current, generationTime: current.generationTime.slice(0, 5) }));
    }
  }

  private emptyDaySelection(): Record<WeekDay, boolean> {
    return {
      Sunday: false,
      Monday: false,
      Tuesday: false,
      Wednesday: false,
      Thursday: false,
      Friday: false,
      Saturday: false,
    };
  }

  private toApiDate(value: string): string | null {
    return value ? new Date(value).toISOString() : null;
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
