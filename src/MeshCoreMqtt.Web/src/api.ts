export type User = { id: string; email: string; name: string; admin: boolean };

const privileges = [
  ["view", "Смотреть сообщения"],
  ["subscribe", "Чтение"],
  ["publish", "Запись"],
  ["credentials", "Логины репитеров"],
  ["topics", "Служебное"],
  ["access", "Управление доступом"]
] as const;

export function privilegeLabel(name: string) {
  return privileges.find((item) => item[0] === name)?.[1] ?? name;
}

export function token() {
  return localStorage.getItem("token");
}

export function setToken(value: string | null) {
  if (value) localStorage.setItem("token", value);
  else localStorage.removeItem("token");
}

export async function api<T>(path: string, options: RequestInit = {}): Promise<T> {
  const headers = new Headers(options.headers);
  if (!(options.body instanceof FormData)) headers.set("Content-Type", "application/json");
  const current = token();
  if (current) headers.set("Authorization", `Bearer ${current}`);
  const response = await fetch(path, { ...options, headers });
  if (response.status === 401 && !path.endsWith("/auth/login")) {
    setToken(null);
    sendToLogin();
    throw new Error("Нужно войти");
  }
  if (response.status === 204) return undefined as T;
  const text = await response.text();
  let body: any = null;
  if (text) {
    try { body = JSON.parse(text); } catch { body = text; }
  }
  if (!response.ok) throw new Error(errorText(body));
  return body as T;
}

export async function download(path: string, filename: string) {
  const headers = new Headers();
  const current = token();
  if (current) headers.set("Authorization", `Bearer ${current}`);
  const response = await fetch(path, { headers });
  if (response.status === 401) {
    setToken(null);
    sendToLogin();
    throw new Error("Нужно войти");
  }
  if (!response.ok) {
    const text = await response.text();
    let body: any = text;
    try { body = JSON.parse(text); } catch { /* текст ответа */ }
    throw new Error(errorText(body));
  }
  const blob = await response.blob();
  const url = URL.createObjectURL(blob);
  const link = document.createElement("a");
  link.href = url;
  link.download = filename;
  link.click();
  URL.revokeObjectURL(url);
}

export function safeReturn(value: string | null, admin = false) {
  if (!value || !value.startsWith("/") || value.startsWith("//") || value.includes("\\") || value.includes("//")) return null;
  const path = value.split("?")[0];
  if (path === "/app" || path.startsWith("/app/")) return value;
  if (admin && (path === "/admin" || path.startsWith("/admin/"))) return value;
  return null;
}

function sendToLogin() {
  if (location.pathname === "/login") return;
  const back = safeReturn(`${location.pathname}${location.search}`, location.pathname === "/admin" || location.pathname.startsWith("/admin/"));
  location.assign(back ? `/login?next=${encodeURIComponent(back)}` : "/login");
}

function errorText(body: any) {
  if (!body) return "Запрос не выполнен";
  if (typeof body === "string") return body;
  if (typeof body.error === "string" && body.error) return body.error;
  if (typeof body.title === "string" && body.title) return body.title;
  const details = body.errors && Object.values(body.errors).flat().find((item) => typeof item === "string");
  return typeof details === "string" ? details : "Запрос не выполнен";
}

