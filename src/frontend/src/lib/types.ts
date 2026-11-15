export type User = { id: string; email: string; displayName: string };
export type Auth = { token: string; user: User };
export type Brief = {
  subject: string; goal: string; audience: string; tone: string; language: string;
  platform: string; targetDurationSeconds: number; callToAction?: string | null;
};
export type BrandKit = { id: string; name: string; themeKey: string; definitionJson: string; isSystem: boolean };
export type Asset = {
  id: string; kind: string; fileName: string; contentType: string; sizeBytes: number;
  durationSeconds?: number | null; width?: number | null; height?: number | null;
  downloadUrl?: string | null; createdAt: string;
};
export type Revision = {
  number: number; parentNumber?: number | null; state: string; summary: string;
  touchesVideo: boolean; touchesCover: boolean; videoAssetId?: string | null; author: string; createdAt: string;
};
export type Cover = { id: string; assetId: string; source: string; headline: string; prompt?: string | null; selected: boolean; downloadUrl?: string | null; revisionNumber: number };
export type Message = { id: string; role: string; content: string; selectionJson?: string | null; changeSetJson?: string | null; revisionNumber?: number | null; createdAt: string };
export type Job = { id: string; kind: string; status: string; stage: string; message?: string | null; attempt: number; cancelRequested: boolean; updatedAt: string };
export type ProjectSummary = { id: string; title: string; status: string; statusDetail?: string | null; platform: string; language: string; updatedAt: string; revision: number };
export type Project = {
  id: string; title: string; status: string; statusDetail?: string | null; concurrencyToken: number;
  currentRevisionNumber: number; currentVideoAssetId?: string | null; selectedCoverId?: string | null;
  brief: Brief; brand?: BrandKit | null; themeSnapshotJson: string; warnings: string[];
  publishPackJson?: string | null; assets: Asset[]; revisions: Revision[]; covers: Cover[];
  messages: Message[]; jobs: Job[]; timeline?: unknown; transcript?: { language?: string; provider?: string; wordTiming?: boolean; text?: string; words?: string } | null;
};
export type SystemInfo = {
  provider: string; model: string; providerConfigured: boolean; tools: boolean;
  imageGenerationConfigured: boolean; transcriptionProvider: string; storageProvider: string;
  engineConfigured: boolean; engineVersion?: string | null; notes: string[];
};

export const tones = ["professional", "funny", "educational", "promotional", "cinematic", "calm"];
export const languages = [
  { id: "fa", label: "Persian" },
  { id: "en", label: "English" },
  { id: "auto", label: "Auto / mixed" },
];
export const platforms = [
  { id: "reel", label: "Reel", ratio: "9:16" },
  { id: "story", label: "Story", ratio: "9:16" },
  { id: "post", label: "Post", ratio: "4:5" },
];

export function themeColors(json?: string) {
  try {
    const parsed = JSON.parse(json || "{}") as { primary?: string; accent?: string; text?: string };
    return { primary: parsed.primary || "#1C1917", accent: parsed.accent || "#C4552A", text: parsed.text || "#F4EFE6" };
  } catch {
    return { primary: "#1C1917", accent: "#C4552A", text: "#F4EFE6" };
  }
}

export function statusLabel(status: string) {
  return status.replace(/([a-z])([A-Z])/g, "$1 $2");
}
