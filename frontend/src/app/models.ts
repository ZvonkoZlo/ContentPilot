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
