export const apiBase = process.env.NEXT_PUBLIC_API_URL || "http://localhost:8080";

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
