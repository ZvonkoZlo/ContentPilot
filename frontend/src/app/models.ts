export interface Tenant {
  id: string;
  name: string;
  slug: string;
  isActive: boolean;
  createdAt: string;
}

export interface Brand {
  id: string;
  name: string;
  timeZoneId: string;
}

export interface Campaign {
  id: string;
  brandId: string;
  weekStart: string;
  trigger: string;
  status: string;
  theme: string | null;
  failureReason: string | null;
  completedAt: string | null;
}

export interface ContentItemSummary {
  id: string;
  ordinal: number;
  type: string;
  topic: string;
  pillar: string;
  publishDay: string;
  status: string;
  qualityAttempts: number;
  failureReason: string | null;
}

export interface ItemFinding {
  code: string;
  severity: string;
  slotId: string | null;
  detail: string;
  measured: number | null;
  threshold: number | null;
  confidence: number | null;
}

export interface ItemFindings {
  attempt: number;
  gate: string;
  outcome: string;
  score: number;
  evaluatedAt: string;
  findings: ItemFinding[];
}

export interface ItemStep {
  stepName: string;
  attempt: number;
  outcome: string;
  startedAt: string;
  completedAt: string | null;
  error: string | null;
}

export interface ItemAgentRun {
  agentName: string;
  attempt: number;
  modelId: string;
  outcome: string;
  costMicroCents: number;
  durationMs: number;
  startedAt: string;
}

export interface ItemDetail extends ContentItemSummary {
  objective: string;
  reviews: ItemFindings[];
  steps: ItemStep[];
  agentRuns: ItemAgentRun[];
}

export interface ItemImage {
  url: string;
  mediaType: string;
  width: number;
  height: number;
}

export interface QaPassRate {
  totalItems: number;
  terminalItems: number;
  approved: number;
  firstAttemptPasses: number;
  needsReview: number;
  failed: number;
  firstAttemptPassRate: number | null;
}

export interface CampaignCostByAgent {
  agentName: string;
  microCents: number;
  calls: number;
}

export interface CampaignCost {
  campaignId: string;
  totalMicroCents: number;
  budgetMicroCents: number;
  remainingMicroCents: number;
  byAgent: CampaignCostByAgent[];
}

export interface CampaignPackage {
  campaignId: string;
  builtAt: string;
  emailSentAt: string | null;
  manifestJson: string;
}

export interface DeadJob {
  id: string;
  type: string;
  tenantId: string | null;
  attempts: number;
  maxAttempts: number;
  lastError: string | null;
  createdAt: string;
  startedAt: string | null;
}

export interface StuckRun {
  id: string;
  tenantId: string;
  scope: string;
  campaignId: string;
  entityId: string;
  deadline: string;
  leaseUntil: string | null;
  leaseOwner: string | null;
  stepsExecuted: number;
}

export interface EvalResult {
  scenarioName: string;
  kind: string;
  passed: boolean;
  detail: string;
  durationMs: number;
}

export interface EvalRun {
  id: string;
  mode: string;
  runAt: string;
  totalScenarios: number;
  passedScenarios: number;
  allPassed: boolean;
  results: EvalResult[];
}

export interface VisualIdentity {
  primaryColor: string;
  secondaryColor: string | null;
  accentColor: string | null;
  darkColor: string;
  lightColor: string;
  headingFont: string;
  bodyFont: string;
  cornerRadius: number;
  styleKeywords: string[];
}

export interface ToneOfVoice {
  summary: string;
  traits: string[];
  avoid: string[];
  preferredCtaStyle: string | null;
  bannedWords: string[];
  forbiddenClaims: string[];
}

export interface Messaging {
  positioning: string;
  principles: string[];
  corePromise: string | null;
}

export interface BrandProfile {
  visual: VisualIdentity;
  voice: ToneOfVoice;
  messaging: Messaging;
  operatorNotes: string | null;
}

export type FactCategory = 'Feature' | 'Pricing' | 'Integration' | 'Availability' | 'Outcome' | 'Company';

export interface ProductFact {
  id: string;
  key: string;
  statement: string;
  category: FactCategory;
  evidence: string | null;
  isPublic: boolean;
  validFrom: string | null;
  validTo: string | null;
}

export interface PersonaDetail {
  pains: string[];
  goals: string[];
  objections: string[];
  vocabulary: string[];
  context: string | null;
}

export interface AudiencePersona {
  id: string;
  name: string;
  segment: string;
  isPrimary: boolean;
  detail: PersonaDetail;
}

export type WeekDay =
  | 'Sunday'
  | 'Monday'
  | 'Tuesday'
  | 'Wednesday'
  | 'Thursday'
  | 'Friday'
  | 'Saturday';

export interface ContentPreferences {
  postsPerWeek: number;
  carouselsPerWeek: number;
  reelsPerWeek: number;
  totalPerWeek: number;
  excludedTopics: string[];
  preferredTopics: string[];
  publishDays: WeekDay[];
  generationDay: WeekDay;
  generationTime: string;
  scheduledGenerationEnabled: boolean;
}

export type AssetKind = 'ProductScreenshot' | 'Logo' | 'Photo' | 'Background' | 'PriorCreative';

export interface BrandAsset {
  id: string;
  kind: string;
  fileName: string;
  mediaType: string;
  width: number;
  height: number;
  bytes: number;
  origin: string;
  tags: string[];
  dominantColors: string[];
  description: string | null;
  isArchived: boolean;
  createdAt: string;
}
