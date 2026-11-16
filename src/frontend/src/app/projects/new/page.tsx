"use client";

import { useEffect, useState } from "react";
import { useRouter } from "next/navigation";
import { Shell } from "@/components/shell";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Textarea } from "@/components/ui/textarea";
import { api } from "@/lib/api";
import { languages, platforms, themeColors, tones, type BrandKit, type Project } from "@/lib/types";

const steps = ["Brief", "Theme", "Footage", "Generate"];

export default function NewProjectPage() {
  const router = useRouter();
  const [step, setStep] = useState(0);
  const [kits, setKits] = useState<BrandKit[]>([]);
  const [title, setTitle] = useState("");
  const [subject, setSubject] = useState("");
  const [goal, setGoal] = useState("");
  const [audience, setAudience] = useState("existing customers");
  const [tone, setTone] = useState("cinematic");
  const [language, setLanguage] = useState("fa");
  const [platform, setPlatform] = useState("reel");
  const [duration, setDuration] = useState(15);
  const [cta, setCta] = useState("");
  const [kitId, setKitId] = useState("");
  const [files, setFiles] = useState<File[]>([]);
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  const [note, setNote] = useState("");

  useEffect(() => {
    if (!localStorage.getItem("lumen-token")) { location.href = "/"; return; }
    api<BrandKit[]>("/api/v1/brand-kits").then(list => { setKits(list); setKitId(list[0]?.id || ""); }).catch(err => setError(err.message));
  }, []);

  const video = files.find(f => /\.(mp4|mov|webm|mkv)$/i.test(f.name));

  async function generate() {
    if (!video) { setError("Add at least one raw video."); return; }
    setBusy(true);
    setError("");
    try {
      setNote("Creating the project…");
      const project = await api<Project>("/api/v1/projects", {
        method: "POST",
        body: JSON.stringify({ title, subject, goal, audience, tone, language, platform, targetDurationSeconds: duration, callToAction: cta || null, brandKitId: kitId }),
      });
      for (const file of files) {
        setNote(`Uploading ${file.name}…`);
        const body = new FormData();
        body.append("file", file);
        body.append("kind", file === video ? "SourceVideo" : kindFor(file));
        await api(`/api/v1/projects/${project.id}/assets`, { method: "POST", body });
      }
      setNote("Queued. You can leave this page.");
      await api(`/api/v1/projects/${project.id}/generate`, { method: "POST", body: "{}" });
      router.push(`/projects/${project.id}`);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Could not start generation.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <Shell>
      <p className="text-xs uppercase tracking-[0.18em] text-[#c4552a]">New draft</p>
      <h1 className="mb-6 font-serif text-4xl">From a raw take to a reviewable cut</h1>
      <ol className="mb-8 flex gap-2 text-sm">
        {steps.map((label, i) => (
          <li key={label} className={`rounded-full px-3 py-1 ${i === step ? "bg-primary text-primary-foreground" : "bg-muted text-muted-foreground"}`}>{i + 1}. {label}</li>
        ))}
      </ol>
      {error && <p className="mb-4 text-sm text-[#ffb4a2]">{error}</p>}
      {step === 0 && (
        <div className="grid gap-4 md:grid-cols-2">
          <Field label="Title"><Input name="title" value={title} onChange={e => setTitle(e.target.value)} placeholder="رشد فروش این ماه" /></Field>
          <Field label="Subject"><Input value={subject} onChange={e => setSubject(e.target.value)} placeholder="What the video is about" /></Field>
          <div className="md:col-span-2"><Field label="Goal"><Textarea value={goal} onChange={e => setGoal(e.target.value)} placeholder="The one thing a viewer should understand" /></Field></div>
          <Field label="Audience"><Input value={audience} onChange={e => setAudience(e.target.value)} /></Field>
          <Field label="Call to action"><Input value={cta} onChange={e => setCta(e.target.value)} placeholder="Optional" /></Field>
          <Choice label="Tone" value={tone} options={tones.map(t => ({ id: t, label: t }))} onChange={setTone} />
          <Choice label="Language" value={language} options={languages} onChange={setLanguage} />
          <Choice label="Platform" value={platform} options={platforms.map(p => ({ id: p.id, label: `${p.label} ${p.ratio}` }))} onChange={setPlatform} />
          <Field label="Target seconds"><Input type="number" min={5} max={90} value={duration} onChange={e => setDuration(Number(e.target.value))} /></Field>
        </div>
      )}
      {step === 1 && (
        <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
          {kits.map(kit => {
            const colors = themeColors(kit.definitionJson);
            return (
              <button key={kit.id} type="button" onClick={() => setKitId(kit.id)} className={`rounded-xl border p-4 text-left ${kitId === kit.id ? "border-[#c4552a]" : "border-border"}`}>
                <div className="mb-3 flex h-24 overflow-hidden rounded-lg" style={{ background: colors.primary }}>
                  <div className="m-auto rounded px-3 py-1 text-sm" style={{ background: colors.accent, color: colors.primary }} dir={language === "fa" ? "rtl" : "ltr"}>
                    {language === "fa" ? "رشد ۳۵٪" : "Growth"}
                  </div>
                </div>
                <p className="font-medium">{kit.name}</p>
                <p className="text-xs text-muted-foreground">{kit.themeKey}</p>
              </button>
            );
          })}
          {kits.length === 0 && <p className="text-muted-foreground">Loading themes…</p>}
        </div>
      )}
      {step === 2 && (
        <div className="space-y-4">
          <label className="flex cursor-pointer flex-col items-center justify-center rounded-xl border border-dashed border-border px-6 py-12 text-center">
            <span className="font-medium">Drop footage, stills, a logo, music, or a transcript</span>
            <span className="mt-2 text-sm text-muted-foreground">At least one MP4, MOV, WebM or MKV. Originals stay immutable. 512 MB limit.</span>
            <input className="mt-4" type="file" multiple accept="video/*,image/*,audio/*,.json,.srt,.vtt,.txt" onChange={e => setFiles([...(e.target.files || [])])} />
          </label>
          <ul className="text-sm text-muted-foreground">
            {files.map(file => <li key={file.name}>{file.name} · {(file.size / 1024 / 1024).toFixed(1)} MB · {kindFor(file)}</li>)}
          </ul>
        </div>
      )}
      {step === 3 && (
        <div className="max-w-xl space-y-3 text-sm leading-6 text-muted-foreground">
          <p className="text-base text-foreground">Generate will queue a background job. The page does not pretend to know a percentage.</p>
          <ol className="list-decimal space-y-1 pl-5">
            <li>Probe the upload.</li>
            <li>Use a supplied transcript or the configured recognizer. Words are not invented.</li>
            <li>Ask the configured model for an editorial plan, or the offline planner when no key is set.</li>
            <li>Cut and reframe in AI Edit Studio, then render a playable preview.</li>
            <li>Compose a cover from a frame. The cover does not rewrite the timeline.</li>
          </ol>
          {note && <p className="text-[#f2c14e]">{note}</p>}
        </div>
      )}
      <div className="mt-8 flex justify-between">
        <Button variant="secondary" disabled={step === 0 || busy} onClick={() => setStep(step - 1)}>Back</Button>
        {step < 3 ? (
          <Button disabled={!canAdvance(step, title, subject, goal, video, files)} onClick={() => setStep(step + 1)}>Continue</Button>
        ) : (
          <Button disabled={busy || !video} onClick={generate}>{busy ? "Queuing…" : "Generate draft"}</Button>
        )}
      </div>
    </Shell>
  );
}

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return <div className="space-y-2"><Label>{label}</Label>{children}</div>;
}

function Choice({ label, value, options, onChange }: { label: string; value: string; options: { id: string; label: string }[]; onChange: (id: string) => void }) {
  return (
    <div className="space-y-2">
      <Label>{label}</Label>
      <div className="flex flex-wrap gap-2">
        {options.map(option => (
          <button type="button" key={option.id} onClick={() => onChange(option.id)} className={`rounded-full px-3 py-1 text-sm ${value === option.id ? "bg-primary text-primary-foreground" : "bg-muted text-muted-foreground"}`}>{option.label}</button>
        ))}
      </div>
    </div>
  );
}

function kindFor(file: File) {
  if (/\.(mp4|mov|webm|mkv)$/i.test(file.name)) return "AdditionalClip";
  if (/\.(png|jpe?g|webp)$/i.test(file.name)) return "Photo";
  if (/\.(mp3|wav|m4a|ogg)$/i.test(file.name)) return "Music";
  if (/\.(json|srt|vtt|txt)$/i.test(file.name)) return "TranscriptFile";
  return "Photo";
}

function canAdvance(step: number, title: string, subject: string, goal: string, video: File | undefined, files: File[]) {
  if (step === 0) return title.trim().length > 1 && subject.trim().length > 1 && goal.trim().length > 1;
  if (step === 2) return Boolean(video);
  return true;
}
