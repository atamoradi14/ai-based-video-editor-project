"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
import { api } from "@/lib/api";
import type { SystemInfo, User } from "@/lib/types";

export function Shell({ children }: { children: React.ReactNode }) {
  const [user, setUser] = useState<User | null>(null);
  const [system, setSystem] = useState<SystemInfo | null>(null);
  useEffect(() => {
    const raw = localStorage.getItem("lumen-user");
    if (raw) setUser(JSON.parse(raw));
    api<SystemInfo>("/api/v1/system").then(setSystem).catch(() => setSystem(null));
  }, []);
  return (
    <div className="min-h-screen">
      <header className="border-b border-border">
        <div className="mx-auto flex max-w-6xl items-center justify-between gap-4 px-4 py-4">
          <Link href="/projects" className="flex items-baseline gap-3">
            <span className="font-serif text-2xl tracking-tight">Lumen</span>
            <span className="text-xs uppercase tracking-[0.18em] text-muted-foreground">Content studio</span>
          </Link>
          <div className="flex items-center gap-3 text-sm text-muted-foreground">
            {user && <span>{user.displayName}</span>}
            <button
              className="underline-offset-4 hover:underline"
              onClick={() => { localStorage.removeItem("lumen-token"); localStorage.removeItem("lumen-user"); location.href = "/"; }}
            >Sign out</button>
          </div>
        </div>
      </header>
      {system && !system.providerConfigured && (
        <div className="border-b border-border bg-[#2a2118] px-4 py-2 text-center text-sm text-[#f2c14e]">
          The model endpoint is not configured. Generation uses the offline fixture planner, then real FFmpeg edits. Set AI_PROVIDER, AI_MODEL and AI_API_KEY to call a live model. {system.model ? `Configured name: ${system.model}.` : ""}
        </div>
      )}
      <main className="mx-auto max-w-6xl px-4 py-8">{children}</main>
    </div>
  );
}
