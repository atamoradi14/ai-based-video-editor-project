"use client";

import { useEffect, useMemo, useState } from "react";
import { useParams } from "next/navigation";
import { Shell } from "@/components/shell";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Textarea } from "@/components/ui/textarea";
import { api, mediaUrl } from "@/lib/api";
import { statusLabel, type Project } from "@/lib/types";

const active = new Set(["Queued", "Analyzing", "Generating", "Rendering", "RevisionInProgress", "Uploading"]);

export default function StudioPage() {
  const params = useParams<{ id: string }>();
  const [project, setProject] = useState<Project | null>(null);
  const [error, setError] = useState("");
  const [start, setStart] = useState(0);
  const [end, setEnd] = useState(3);
  const [kind, setKind] = useState("range");
  const [note, setNote] = useState("");
  const [busy, setBusy] = useState(false);

  async function load() {
    try {
      const next = await api<Project>(`/api/v1/projects/${params.id}`);
      setProject(next);
      setError("");
    } catch (err) {
      setError(err instanceof Error ? err.message : "Could not load the project.");
    }
  }

  useEffect(() => {
    if (!localStorage.getItem("lumen-token")) { location.href = "/"; return; }
    load();
    const timer = setInterval(() => {
      setProject(current => {
        if (current && active.has(current.status)) load();
        return current;
      });
    }, 2000);
    return () => clearInterval(timer);
  }, [params.id]);

  const video = useMemo(() => project?.assets.filter(a => a.kind === "DerivedVideo").at(-1), [project]);
  const captions = project?.assets.filter(a => a.kind === "CaptionFile").at(-1);
  const cover = project?.covers.find(c => c.selected) || project?.covers[0];
  const job = project?.jobs[0];
  const working = project ? active.has(project.status) : false;

  async function correct() {
    if (!project) return;
    setBusy(true);
    setError("");
    try {
      await api(`/api/v1/projects/${project.id}/corrections`, {
        method: "POST",
        body: JSON.stringify({
          instruction: note,
          baseRevision: project.currentRevisionNumber,
          selection: { kind, start: kind === "cover" || kind === "project" ? null : start, end: kind === "cover" || kind === "project" ? null : end, clipId: null, index: null, coverId: kind === "cover" ? cover?.id ?? null : null },
        }),
      });
      setNote("");
      await load();
    } catch (err) {
      setError(err instanceof Error ? err.message : "The correction was not accepted.");
    } finally {
      setBusy(false);
    }
  }

  async function act(path: string) {
    setBusy(true);
    try { await api(path, { method: "POST", body: "{}" }); await load(); }
    catch (err) { setError(err instanceof Error ? err.message : "Request failed."); }
    finally { setBusy(false); }
  }

  if (!project && !error) return <Shell><p className="text-muted-foreground">Opening the draft…</p></Shell>;
  if (!project) return <Shell><p className="text-[#ffb4a2]">{error}</p></Shell>;

  return (
    <Shell>
      <div className="mb-6 flex flex-col justify-between gap-3 sm:flex-row sm:items-end">
        <div>
          <p className="text-xs uppercase tracking-[0.18em] text-[#c4552a]">{project.brief.platform} · {project.brief.language}</p>
          <h1 className="font-serif text-4xl" dir={project.brief.language === "fa" ? "rtl" : "ltr"}>{project.title}</h1>
          <p className="mt-1 text-sm text-muted-foreground">{project.statusDetail || statusLabel(project.status)}</p>
        </div>
        <Badge>{statusLabel(project.status)}</Badge>
      </div>
      {error && <p className="mb-4 text-sm text-[#ffb4a2]">{error}</p>}
      {project.warnings.length > 0 && (
        <ul className="mb-4 space-y-1 rounded-lg border border-[#5c4a22] bg-[#2a2118] p-3 text-sm text-[#f2c14e]">
          {project.warnings.map((warning, i) => <li key={i}>{warning}</li>)}
        </ul>
      )}
      {working && job && (
        <div className="mb-4 rounded-lg border border-border p-4">
          <p className="text-sm">{job.message || job.stage}</p>
          <p className="mt-1 text-xs text-muted-foreground">Stage {job.stage} · attempt {job.attempt}. This is the worker’s last report, not a guessed percentage.</p>
          <Button className="mt-3" variant="secondary" size="sm" onClick={() => act(`/api/v1/jobs/${job.id}/cancel`)}>Cancel job</Button>
        </div>
      )}
      <div className="grid gap-6 lg:grid-cols-[minmax(0,1fr)_320px]">
        <section className="space-y-4">
          <div className="overflow-hidden rounded-xl bg-black">
            {video?.downloadUrl ? (
              <video key={video.id} className="mx-auto max-h-[70vh]" controls src={mediaUrl(video.downloadUrl)} />
            ) : (
              <div className="flex aspect-[9/16] max-h-[70vh] items-center justify-center text-sm text-[#b7aa9a]">The playable preview appears after generation.</div>
            )}
          </div>
          <div className="rounded-xl border border-border p-4">
            <div className="mb-3 flex flex-wrap gap-2">
              {["project", "range", "subtitle", "audio", "cover"].map(item => (
                <button key={item} className={`rounded-full px-3 py-1 text-sm ${kind === item ? "bg-primary text-primary-foreground" : "bg-muted"}`} onClick={() => setKind(item)}>{item}</button>
              ))}
            </div>
            {kind !== "cover" && kind !== "project" && (
              <div className="mb-3 grid grid-cols-2 gap-3 text-sm">
                <label>From <input className="mt-1 w-full rounded-md border border-border bg-transparent px-2 py-1" type="number" min={0} step={0.1} value={start} onChange={e => setStart(Number(e.target.value))} /></label>
                <label>To <input className="mt-1 w-full rounded-md border border-border bg-transparent px-2 py-1" type="number" min={0} step={0.1} value={end} onChange={e => setEnd(Number(e.target.value))} /></label>
              </div>
            )}
            <Textarea value={note} onChange={e => setNote(e.target.value)} placeholder={kind === "cover" ? "Make only the cover more professional. Leave the video alone." : "Remove the pause in this range, or change only the subtitle here."} />
            <Button className="mt-3" disabled={busy || working || note.trim().length < 3} onClick={correct}>Ask for this change</Button>
          </div>
          <Transcript project={project} />
        </section>
        <aside className="space-y-4">
          <div className="rounded-xl border border-border p-4">
            <p className="mb-2 text-xs uppercase tracking-[0.16em] text-muted-foreground">Cover</p>
            {cover?.downloadUrl ? <img alt={cover.headline} className="aspect-[9/16] w-full rounded-lg object-cover" src={mediaUrl(cover.downloadUrl)} /> : <p className="text-sm text-muted-foreground">No cover yet.</p>}
            {cover && <p className="mt-2 text-sm" dir="auto">{cover.headline}</p>}
            <p className="mt-1 text-xs text-muted-foreground">{cover ? `${cover.source} · revision ${cover.revisionNumber}` : ""}</p>
          </div>
          <div className="rounded-xl border border-border p-4">
            <p className="mb-2 text-xs uppercase tracking-[0.16em] text-muted-foreground">Revisions</p>
            <ul className="space-y-3 text-sm">
              {project.revisions.map(revision => (
                <li key={revision.number}>
                  <p>#{revision.number} {revision.summary}</p>
                  <p className="text-xs text-muted-foreground">{revision.state} · video {revision.touchesVideo ? "changed" : "unchanged"} · cover {revision.touchesCover ? "changed" : "unchanged"}</p>
                  {revision.number === project.currentRevisionNumber && revision.number > 1 && (
                    <div className="mt-1 flex gap-2">
                      <Button size="sm" variant="secondary" disabled={busy} onClick={() => act(`/api/v1/projects/${project.id}/revisions/${revision.number}/accept`)}>Accept</Button>
                      <Button size="sm" variant="ghost" disabled={busy} onClick={() => act(`/api/v1/projects/${project.id}/revisions/${revision.number}/reject`)}>Reject</Button>
                    </div>
                  )}
                  {revision.parentNumber && (
                    <button className="mt-1 text-xs underline" onClick={() => act(`/api/v1/projects/${project.id}/revisions/${revision.parentNumber}/restore`)}>Restore parent</button>
                  )}
                </li>
              ))}
              {project.revisions.length === 0 && <li className="text-muted-foreground">No revisions yet.</li>}
            </ul>
          </div>
          <div className="rounded-xl border border-border p-4 text-sm">
            <p className="mb-2 text-xs uppercase tracking-[0.16em] text-muted-foreground">Export</p>
            <Button disabled={busy || working || !video} onClick={() => act(`/api/v1/projects/${project.id}/export`)}>Package video, cover and captions</Button>
            <div className="mt-3 flex flex-col gap-1">
              {video?.downloadUrl && <a className="underline" href={mediaUrl(video.downloadUrl)}>Download preview MP4</a>}
              {cover?.downloadUrl && <a className="underline" href={mediaUrl(cover.downloadUrl)}>Download cover</a>}
              {captions?.downloadUrl && <a className="underline" href={mediaUrl(captions.downloadUrl)}>Download captions</a>}
            </div>
            {project.publishPackJson && <Publish pack={project.publishPackJson} />}
          </div>
          <div className="rounded-xl border border-border p-4">
            <p className="mb-2 text-xs uppercase tracking-[0.16em] text-muted-foreground">Notes</p>
            <ul className="space-y-2 text-sm">
              {project.messages.map(message => <li key={message.id} dir="auto"><span className="text-muted-foreground">{message.role}: </span>{message.content}</li>)}
              {project.messages.length === 0 && <li className="text-muted-foreground">Corrections you ask for show up here with the typed change.</li>}
            </ul>
          </div>
        </aside>
      </div>
    </Shell>
  );
}

function Transcript({ project }: { project: Project }) {
  let words: { start: number; end: number; text: string }[] = [];
  try {
    const raw = project.transcript?.words;
    words = typeof raw === "string" ? JSON.parse(raw) : [];
  } catch { words = []; }
  if (!words.length && !project.transcript?.text) return null;
  return (
    <div className="rounded-xl border border-border p-4">
      <p className="mb-2 text-xs uppercase tracking-[0.16em] text-muted-foreground">Transcript · {project.transcript?.provider || "stored"}{project.transcript?.wordTiming ? "" : " · no word timing"}</p>
      <p className="text-sm leading-7" dir="auto">
        {words.length ? words.map((word, i) => <span key={i} title={`${word.start.toFixed(1)}–${word.end.toFixed(1)}`}> {word.text}</span>) : project.transcript?.text}
      </p>
    </div>
  );
}

function Publish({ pack }: { pack: string }) {
  try {
    const parsed = JSON.parse(pack) as { caption?: string; hashtags?: string[]; title?: string };
    return (
      <div className="mt-3 space-y-1 text-muted-foreground" dir="auto">
        {parsed.title && <p>{parsed.title}</p>}
        {parsed.caption && <p>{parsed.caption}</p>}
        {parsed.hashtags && <p>{parsed.hashtags.join(" ")}</p>}
      </div>
    );
  } catch {
    return null;
  }
}
