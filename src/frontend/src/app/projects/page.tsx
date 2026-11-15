"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
import { Shell } from "@/components/shell";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { api } from "@/lib/api";
import { statusLabel, type ProjectSummary } from "@/lib/types";

export default function ProjectsPage() {
  const [q, setQ] = useState("");
  const [items, setItems] = useState<ProjectSummary[] | null>(null);
  const [error, setError] = useState("");

  async function load(query = q) {
    setError("");
    try {
      setItems(await api<ProjectSummary[]>(`/api/v1/projects?q=${encodeURIComponent(query)}&sort=updated`));
    } catch (err) {
      setError(err instanceof Error ? err.message : "Could not load projects.");
      setItems([]);
    }
  }

  useEffect(() => { if (!localStorage.getItem("lumen-token")) { location.href = "/"; return; } load(""); }, []);

  return (
    <Shell>
      <div className="mb-8 flex flex-col justify-between gap-4 sm:flex-row sm:items-end">
        <div>
          <p className="text-xs uppercase tracking-[0.18em] text-[#c4552a]">Library</p>
          <h1 className="font-serif text-4xl">Projects</h1>
        </div>
        <Button render={<Link href="/projects/new" />}>New project</Button>
      </div>
      <form className="mb-6 flex gap-2" onSubmit={e => { e.preventDefault(); load(); }}>
        <Input value={q} onChange={e => setQ(e.target.value)} placeholder="Search title or subject" />
        <Button type="submit" variant="secondary">Search</Button>
      </form>
      {error && <p className="mb-4 text-sm text-[#ffb4a2]">{error}</p>}
      {items === null && <p className="text-muted-foreground">Loading the library…</p>}
      {items?.length === 0 && <p className="rounded-xl border border-dashed border-border p-8 text-muted-foreground">No projects yet. Start with a raw video and a subject.</p>}
      <ul className="divide-y divide-border rounded-xl border border-border">
        {items?.map(item => (
          <li key={item.id}>
            <Link href={`/projects/${item.id}`} className="flex flex-col gap-2 px-4 py-4 hover:bg-muted/40 sm:flex-row sm:items-center sm:justify-between">
              <div>
                <p className="font-medium">{item.title}</p>
                <p className="text-sm text-muted-foreground">{item.platform} · {item.language} · revision {item.revision}</p>
              </div>
              <div className="flex items-center gap-3">
                <Badge variant="secondary">{statusLabel(item.status)}</Badge>
                <span className="text-xs text-muted-foreground">{new Date(item.updatedAt).toLocaleString()}</span>
              </div>
            </Link>
          </li>
        ))}
      </ul>
    </Shell>
  );
}
