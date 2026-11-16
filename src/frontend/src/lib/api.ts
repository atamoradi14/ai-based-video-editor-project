// Empty means same-origin. The published site serves /api/v1 itself.
// A self-hosted UI sets NEXT_PUBLIC_API_URL to the ASP.NET API.
const configured = (process.env.NEXT_PUBLIC_API_URL || "").replace(/\/$/, "");

export function apiBaseFor(hostname: string) {
  const pageIsLocal = hostname === "localhost" || hostname === "127.0.0.1";
  const apiIsLocal = configured === "" || configured.includes("localhost") || configured.includes("127.0.0.1");
  // A public page must not call the visitor's own computer.
  if (!pageIsLocal && apiIsLocal) return "";
  return configured;
}

export const apiBase = typeof window === "undefined" ? configured : apiBaseFor(window.location.hostname);

export function mediaUrl(url: string | null | undefined) {
  if (!url) return "";
  if (url.startsWith("http://") || url.startsWith("https://")) return url;
  return apiBase + url;
}

export function token() {
  if (typeof window === "undefined") return "";
  return localStorage.getItem("lumen-token") || "";
}

export async function api<T>(path: string, init: RequestInit = {}): Promise<T> {
  const headers = new Headers(init.headers);
  if (!(init.body instanceof FormData) && !headers.has("content-type") && init.body) headers.set("content-type", "application/json");
  const auth = token();
  if (auth) headers.set("authorization", `Bearer ${auth}`);
  const res = await fetch(apiBase + path, { ...init, headers });
  if (res.status === 401) {
    localStorage.removeItem("lumen-token");
    if (typeof window !== "undefined" && !window.location.pathname.startsWith("/login")) window.location.href = "/";
  }
  if (!res.ok) {
    const text = await res.text();
    let message = text || res.statusText;
    try {
      const body = JSON.parse(text);
      message = body.message || body.error?.message || body.error || message;
    } catch { /* plain text */ }
    throw new Error(message);
  }
  if (res.status === 204) return undefined as T;
  return res.json() as Promise<T>;
}
