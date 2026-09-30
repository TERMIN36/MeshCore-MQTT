import { useEffect, useState, type FormEvent } from "react";
import { Navigate, NavLink, Route, Routes, useLocation, useNavigate } from "react-router-dom";
import { api, safeReturn, setToken, token, type User } from "./api";
import { AccountPage, AccountsPage, ActivityPage, CertificatePage, NodesPage, SharingPage } from "./admin";
import { GroupsPage, PanelBar } from "./user";

export function App() {
  const [user, setUser] = useState<User | null>(null);
  const [ready, setReady] = useState(!token());

  useEffect(() => {
    if (!token()) return;
    api<User>("/api/auth/me").then(setUser).catch(() => setUser(null)).finally(() => setReady(true));
  }, []);

  if (!ready) return <main>Загрузка…</main>;
  return (
    <Routes>
      <Route path="/login" element={<LoginGate user={user} onUser={setUser} />} />
      <Route path="/app/*" element={user ? <UserShell email={user.email} /> : <Navigate to={loginTarget()} replace />} />
      <Route path="/admin/*" element={user?.admin ? <AdminShell user={user} /> : <Navigate to={user ? "/app" : loginTarget()} replace />} />
      <Route path="*" element={<Navigate to={user ? (user.admin ? "/admin" : "/app") : "/login"} replace />} />
    </Routes>
  );
}

function loginTarget() {
  const next = safeReturn(`${location.pathname}${location.search}`, location.pathname === "/admin" || location.pathname.startsWith("/admin/"));
  return next ? `/login?next=${encodeURIComponent(next)}` : "/login";
}

function LoginGate({ user, onUser }: { user: User | null; onUser: (user: User) => void }) {
  const location = useLocation();
  const next = safeReturn(new URLSearchParams(location.search).get("next"), !!user?.admin);
  if (user) return <Navigate to={next ?? (user.admin ? "/admin" : "/app")} replace />;
  return <Login onUser={onUser} />;
}

function Login({ onUser }: { onUser: (user: User) => void }) {
  const navigate = useNavigate();
  const location = useLocation();
  const [email, setEmail] = useState("admin@localhost");
  const [password, setPassword] = useState("");
  const [error, setError] = useState("");

  async function submit(event: FormEvent) {
    event.preventDefault();
    setError("");
    try {
      const result = await api<{ token: string; user: User }>("/api/auth/login", {
        method: "POST",
        body: JSON.stringify({ email, password })
      });
      setToken(result.token);
      onUser(result.user);
      const next = safeReturn(new URLSearchParams(location.search).get("next"), result.user.admin);
      navigate(next ?? (result.user.admin ? "/admin" : "/app"));
    } catch (err) {
      setError(err instanceof Error ? err.message : "Ошибка входа");
    }
  }

  return (
    <form className="card login" onSubmit={submit}>
      <div className="brand">
        <img className="brand-mark" src="/icon.svg" alt="" />
        <strong>MeshCore</strong>
      </div>
      <p className="muted">Подключение репитеров и доступ к ним.</p>
      <label>Почта<input value={email} onChange={(e) => setEmail(e.target.value)} autoComplete="username" /></label>
      <label>Пароль<input type="password" value={password} onChange={(e) => setPassword(e.target.value)} autoComplete="current-password" /></label>
      {error && <p className="error">{error}</p>}
      <button type="submit">Войти</button>
    </form>
  );
}

function UserShell({ email }: { email: string }) {
  return (
    <main className="app-main">
      <GroupsPage email={email} onLogout={() => { setToken(null); location.assign("/login"); }} />
    </main>
  );
}

function AdminShell({ user }: { user: User }) {
  return (
    <div className="shell">
      <nav>
        <div className="muted">{user.email}</div>
        <NavLink to="/admin" end>Учётки</NavLink>
        <NavLink to="/admin/sharing">Доступ</NavLink>
        <NavLink to="/admin/activity">Активность</NavLink>
        <NavLink to="/admin/nodes">Узлы</NavLink>
        <NavLink to="/admin/certificate">Сертификат</NavLink>
        <NavLink to="/app">Панель пользователя</NavLink>
        <button className="secondary" onClick={() => { setToken(null); location.assign("/login"); }}>Выйти</button>
      </nav>
      <div className="center">
        <PanelBar />
        <main>
          <Routes>
            <Route index element={<AccountsPage />} />
            <Route path="users/:id" element={<AccountPage />} />
            <Route path="sharing" element={<SharingPage />} />
            <Route path="activity" element={<ActivityPage />} />
            <Route path="nodes" element={<NodesPage />} />
            <Route path="certificate" element={<CertificatePage />} />
          </Routes>
        </main>
        <footer className="bottombar">Подключение репитеров и доступ к ним.</footer>
      </div>
    </div>
  );
}
