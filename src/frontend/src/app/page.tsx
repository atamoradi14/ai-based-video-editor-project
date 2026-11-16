"use client";

import { useState } from "react";
import { useRouter } from "next/navigation";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { api } from "@/lib/api";
import type { Auth } from "@/lib/types";

export default function HomePage() {
  const router = useRouter();
  const [mode, setMode] = useState<"login" | "register">("login");
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [name, setName] = useState("");
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);

  async function submit(e: React.FormEvent<HTMLFormElement>) {
    e.preventDefault();
    const data = new FormData(e.currentTarget);
    const nextEmail = String(data.get("email") || email);
    const nextPassword = String(data.get("password") || password);
    const nextName = String(data.get("displayName") || name);
    setBusy(true);
    setError("");
    try {
      const path = mode === "login" ? "/api/v1/auth/login" : "/api/v1/auth/register";
      const body = mode === "login"
        ? { email: nextEmail, password: nextPassword }
        : { email: nextEmail, password: nextPassword, displayName: nextName || nextEmail };
      const auth = await api<Auth>(path, { method: "POST", body: JSON.stringify(body) });
      localStorage.setItem("lumen-token", auth.token);
      localStorage.setItem("lumen-user", JSON.stringify(auth.user));
      router.push("/projects");
    } catch (err) {
      setError(err instanceof Error ? err.message : "Sign-in failed.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="grid min-h-screen lg:grid-cols-2">
      <section className="flex flex-col justify-between bg-[#1c1713] px-8 py-10 lg:px-14">
        <p className="text-xs uppercase tracking-[0.22em] text-[#c4552a]">Lumen</p>
        <div className="max-w-md space-y-6">
          <h1 className="font-serif text-5xl leading-[1.05] text-[#f4efe6]">A draft you can argue with.</h1>
          <p className="text-lg text-[#d9cfc2]">
            Upload a raw take, name the subject, and pick a theme. The studio cuts a vertical draft, writes a cover, and lets you correct one range — or only the cover — without throwing the rest away.
          </p>
          <p className="text-sm text-[#b7aa9a]" dir="rtl">فارسی و انگلیسی، با زیرنویس قابل ویرایش.</p>
        </div>
        <p className="text-xs text-[#8d8174]">Nothing is published to Instagram from here.</p>
      </section>
      <section className="flex items-center justify-center px-6 py-16">
        <form onSubmit={submit} className="w-full max-w-sm space-y-5">
          <div>
            <h2 className="font-serif text-3xl">{mode === "login" ? "Sign in" : "Create an account"}</h2>
            <p className="mt-2 text-sm text-muted-foreground">Create an account. Each person has their own projects.</p>
          </div>
          {mode === "register" && (
            <div className="space-y-2">
              <Label htmlFor="name">Name</Label>
              <Input id="name" name="displayName" value={name} onChange={e => setName(e.target.value)} />
            </div>
          )}
          <div className="space-y-2">
            <Label htmlFor="email">Email</Label>
            <Input id="email" name="email" type="email" required value={email} onChange={e => setEmail(e.target.value)} />
          </div>
          <div className="space-y-2">
            <Label htmlFor="password">Password</Label>
            <Input id="password" name="password" type="password" required minLength={8} value={password} onChange={e => setPassword(e.target.value)} />
          </div>
          {error && <p className="text-sm text-[#ffb4a2]">{error}</p>}
          <Button type="submit" className="w-full" disabled={busy}>{busy ? "Working…" : mode === "login" ? "Enter the studio" : "Create account"}</Button>
          <button type="button" className="text-sm text-muted-foreground underline-offset-4 hover:underline" onClick={() => setMode(mode === "login" ? "register" : "login")}>
            {mode === "login" ? "Need an account?" : "Already registered?"}
          </button>
        </form>
      </section>
    </div>
  );
}
