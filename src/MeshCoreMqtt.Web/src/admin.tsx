import { useEffect, useState } from "react";
import { Link, useParams } from "react-router-dom";
import { api, download, privilegeLabel } from "./api";
import { ConfirmDialog } from "./user";

export function AccountsPage() {
  const [users, setUsers] = useState<any[]>([]);
  const [email, setEmail] = useState("");
  const [name, setName] = useState("");
  const [password, setPassword] = useState("");
  const [admin, setAdmin] = useState(false);
  const [error, setError] = useState("");

  async function load() { setUsers(await api("/api/admin/users")); }
  useEffect(() => { load().catch((err) => setError(err.message)); }, []);

  return (
    <>
      <h1>Учётки</h1>
      {error && <p className="error">{error}</p>}
      <form className="panel grid" onSubmit={async (event) => {
        event.preventDefault();
        setError("");
        try {
          await api("/api/admin/users", { method: "POST", body: JSON.stringify({ email, name, password, admin }) });
          setEmail(""); setName(""); setPassword(""); setAdmin(false);
          await load();
        } catch (err) {
          setError(err instanceof Error ? err.message : "Не удалось создать учётку");
        }
      }}>
        <input placeholder="Почта" type="email" required value={email} onChange={(e) => setEmail(e.target.value)} />
        <input placeholder="Имя" value={name} onChange={(e) => setName(e.target.value)} />
        <input placeholder="Пароль, не короче 8 символов" type="password" required value={password} onChange={(e) => setPassword(e.target.value)} />
        <label className="check"><input type="checkbox" checked={admin} onChange={(e) => setAdmin(e.target.checked)} />Администратор</label>
        <button type="submit">Создать учётку</button>
      </form>
      <table>
        <tbody>
          {users.map((user) => (
            <tr key={user.id}>
              <td><Link to={`/admin/users/${user.id}`}>{user.name}</Link><div className="muted">{user.email}</div></td>
              <td>{user.admin ? "админ" : "пользователь"}{user.disabled ? " · отключена" : ""}</td>
              <td>
                <button className="secondary" onClick={async () => {
                  setError("");
                  try {
                    await api(`/api/admin/users/${user.id}`, { method: "PATCH", body: JSON.stringify({ disabled: !user.disabled }) });
                    await load();
                  } catch (err) {
                    setError(err instanceof Error ? err.message : "Не удалось изменить учётку");
                  }
                }}>{user.disabled ? "Включить" : "Отключить"}</button>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </>
  );
}

export function AccountPage() {
  const { id = "" } = useParams();
  const [tree, setTree] = useState<any>(null);
  const [error, setError] = useState("");
  useEffect(() => { api(`/api/admin/users/${id}`).then(setTree).catch((err) => setError(err.message)); }, [id]);
  if (!tree) return error ? <p className="error">{error}</p> : <p>Загрузка…</p>;
  return (
    <>
      <h1>{tree.name}</h1>
      <p className="muted">{tree.email}</p>
      <h2>Группы</h2>
      {tree.groups.map((group: any) => (
        <section key={group.id} className="panel">
          <strong>{group.name}</strong>
          {group.spaces.map((space: any) => (
            <div key={space.id}>
              {space.name} · узел {space.node}
            </div>
          ))}
        </section>
      ))}
      <h2>Выданный доступ</h2>
      <table>
        <tbody>
          {tree.grants.map((grant: any) => (
            <tr key={grant.id}>
              <td>{grant.group}</td>
              <td>{grant.tunnels?.length ? grant.tunnels.map((item: any) => `${item.name}: ${item.privileges.filter((flag: string) => flag !== "topics").map(privilegeLabel).join(", ")}`).join("; ") : "тунели не отмечены"}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </>
  );
}

export function SharingPage() {
  const [rows, setRows] = useState<any[]>([]);
  const [error, setError] = useState("");
  useEffect(() => { api<any[]>("/api/admin/sharing").then(setRows).catch((err) => setError(err.message)); }, []);
  return (
    <>
      <h1>Схема доступа</h1>
      {error && <p className="error">{error}</p>}
      <table>
        <thead><tr><th>Кому</th><th>Группа</th><th>Тунели</th></tr></thead>
        <tbody>
          {rows.map((row) => (
            <tr key={row.id}>
              <td>{row.name}<div className="muted">{row.email}</div></td>
              <td>{row.group}</td>
              <td>{row.tunnels?.length ? row.tunnels.map((item: any) => `${item.name}: ${item.privileges.filter((flag: string) => flag !== "topics").map(privilegeLabel).join(", ")}`).join("; ") : "не отмечены"}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </>
  );
}

export function ActivityPage() {
  const [rows, setRows] = useState<any[]>([]);
  const [error, setError] = useState("");
  useEffect(() => { api<any[]>("/api/admin/activity").then(setRows).catch((err) => setError(err.message)); }, []);
  return (
    <>
      <h1>Сообщения</h1>
      {error && <p className="error">{error}</p>}
      <table>
        <thead><tr><th>Публичный ключ</th><th>В минуту</th><th>Тунели</th></tr></thead>
        <tbody>
          {rows.map((row) => (
            <tr key={`${row.node}-${row.topic}`}>
              <td>{row.topic}<div className="muted">{row.node}</div></td>
              <td>{row.messagesPerMinute}</td>
              <td>{row.spaces.map((space: any) => `${space.group} / ${space.name}`).join(", ")}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </>
  );
}

export function CertificatePage() {
  const [info, setInfo] = useState<any>(null);
  const [host, setHost] = useState("");
  const [pem, setPem] = useState("");
  const [error, setError] = useState("");
  const [confirm, setConfirm] = useState(false);
  const [confirmKey, setConfirmKey] = useState(false);

  async function load() {
    const next = await api<any>("/api/admin/certificate");
    setInfo(next);
    setHost(next.host ?? "");
  }

  useEffect(() => { load().catch((err) => setError(err.message)); }, []);

  const until = info?.notAfter ? new Date(info.notAfter).toLocaleString("ru-RU") : "";
  return (
    <>
      <h1>Сертификат</h1>
      {error && <p className="error">{error}</p>}
      {info && (
        <section className="panel">
          <p>Имя: {info.host}</p>
          <p className="muted">Действует до {until}. В сертификате: {(info.names ?? []).join(", ")}</p>
          <form className="grid" onSubmit={(event) => { event.preventDefault(); setError(""); setConfirm(true); }}>
            <input placeholder="Доменное имя или IP" value={host} onChange={(e) => setHost(e.target.value)} required />
            <button type="submit">Выпустить новый сертификат</button>
          </form>
          <p className="muted">Новый сертификат сервера подписывается прежним CA. Уже настроенные репитеры доверяют этому CA, но в их строке настройки остаётся старое имя: подключаться они должны к имени нового сертификата. Новые строки получают его сами. Текущие сессии дорабатывают со старым сертификатом до переподключения.</p>
        </section>
      )}
      {info && (
        <section className="panel">
          <h2>Приватный ключ</h2>
          <div className="row">
            <button className="secondary" type="button" onClick={() => {
              setError("");
              download("/api/admin/certificate/key", "server.key").catch((err) => setError(err instanceof Error ? err.message : "Не удалось скачать ключ"));
            }}>Скачать приватный ключ</button>
          </div>
          <form className="grid" onSubmit={(event) => { event.preventDefault(); setError(""); setConfirmKey(true); }}>
            <textarea placeholder="PEM приватного ключа" value={pem} onChange={(e) => setPem(e.target.value)} required />
            <button type="submit">Импортировать ключ</button>
          </form>
          <p className="muted">Файл — секрет сервера. Импорт заменяет ключ и выпускает сертификат на текущее имя, подпись CA прежняя. Прокси подхватывает его сразу, текущие сессии дорабатывают со старым ключом до переподключения.</p>
        </section>
      )}
      {confirm && (
        <ConfirmDialog
          title="Выпустить сертификат"
          text={`Сертификат сервера будет выпущен заново на «${host.trim()}».`}
          confirmLabel="Выпустить"
          onClose={() => setConfirm(false)}
          onConfirm={async () => {
            try {
              const next = await api<any>("/api/admin/certificate", { method: "POST", body: JSON.stringify({ host }) });
              setInfo(next);
              setHost(next.host ?? host);
              setConfirm(false);
            } catch (err) {
              setError(err instanceof Error ? err.message : "Не удалось выпустить сертификат");
              setConfirm(false);
            }
          }}
        />
      )}
      {confirmKey && (
        <ConfirmDialog
          title="Импортировать ключ"
          text="Текущий приватный ключ сервера будет заменён. Сертификат на то же имя выпустится заново."
          confirmLabel="Импортировать"
          onClose={() => setConfirmKey(false)}
          onConfirm={async () => {
            try {
              const next = await api<any>("/api/admin/certificate/key", { method: "POST", body: JSON.stringify({ pem }) });
              setInfo(next);
              setHost(next.host ?? host);
              setPem("");
              setConfirmKey(false);
            } catch (err) {
              setError(err instanceof Error ? err.message : "Не удалось импортировать ключ");
              setConfirmKey(false);
            }
          }}
        />
      )}
    </>
  );
}

export function NodesPage() {
  const [nodes, setNodes] = useState<any[]>([]);
  const [name, setName] = useState("");
  const [host, setHost] = useState("");
  const [port, setPort] = useState(1883);
  const [tls, setTls] = useState(false);
  const [error, setError] = useState("");

  async function load() { setNodes(await api("/api/admin/nodes")); }
  useEffect(() => { load().catch((err) => setError(err.message)); }, []);

  return (
    <>
      <h1>Узлы брокера</h1>
      {error && <p className="error">{error}</p>}
      <form className="panel grid" onSubmit={async (event) => {
        event.preventDefault();
        setError("");
        try {
          await api("/api/admin/nodes", { method: "POST", body: JSON.stringify({ name, host, port: Number(port), tls }) });
          setName(""); setHost("");
          await load();
        } catch (err) {
          setError(err instanceof Error ? err.message : "Ошибка");
        }
      }}>
        <input placeholder="Имя" value={name} onChange={(e) => setName(e.target.value)} />
        <input placeholder="Внутренний адрес" value={host} onChange={(e) => setHost(e.target.value)} />
        <input type="number" value={port} onChange={(e) => setPort(Number(e.target.value))} />
        <label className="check"><input type="checkbox" checked={tls} onChange={(e) => setTls(e.target.checked)} />TLS до узла</label>
        <button type="submit">Подключить узел</button>
      </form>
      {nodes.map((node) => <NodeCard key={node.id} node={node} nodes={nodes} onChanged={load} onError={setError} />)}
    </>
  );
}

function NodeCard({ node, nodes, onChanged, onError }: { node: any; nodes: any[]; onChanged: () => Promise<void>; onError: (value: string) => void }) {
  const runtime = node.runtime ?? {};
  const [confirmDelete, setConfirmDelete] = useState(false);
  return (
    <section className="panel">
      <h2>{node.name}</h2>
      <p className="muted">{node.host}:{node.port} · {node.status === "open" ? "принимает" : node.status === "draining" ? "только перенос" : "выключен"} · {runtime.reachable ? "на связи" : "нет связи"} · соединений {runtime.connections ?? 0} · сообщений/мин {runtime.messagesPerMinute ?? 0}</p>
      {runtime.error && <p className="muted">{runtime.error}</p>}
      <div className="row">
        {[["open", "Принимает"], ["draining", "Только перенос"], ["offline", "Выключен"]].map(([status, label]) => (
          <button key={status} className="secondary" disabled={node.status === status} onClick={async () => {
            await api(`/api/admin/nodes/${node.id}`, { method: "PATCH", body: JSON.stringify({ status }) });
            await onChanged();
          }}>{label}</button>
        ))}
        <button className="danger" onClick={() => setConfirmDelete(true)}>Удалить</button>
      </div>
      {confirmDelete && (
        <ConfirmDialog
          title="Удалить узел"
          text={`Узел «${node.name}» будет удалён.`}
          confirmLabel="Удалить"
          onClose={() => setConfirmDelete(false)}
          onConfirm={async () => {
            try {
              await api(`/api/admin/nodes/${node.id}`, { method: "DELETE" });
              setConfirmDelete(false);
              await onChanged();
            } catch (err) {
              onError(err instanceof Error ? err.message : "Ошибка");
            }
          }}
        />
      )}
      <table>
        <tbody>
          {node.spaces.map((space: any) => <SpaceMove key={space.id} space={space} nodes={nodes} current={node.id} onChanged={onChanged} onError={onError} />)}
        </tbody>
      </table>
    </section>
  );
}

function SpaceMove({ space, nodes, current, onChanged, onError }: { space: any; nodes: any[]; current: string; onChanged: () => Promise<void>; onError: (value: string) => void }) {
  const [target, setTarget] = useState("");
  const choices = nodes.filter((node) => node.id !== current && node.status !== "offline");
  return (
    <tr>
      <td>{space.group} / {space.name}{space.moving ? " · перенос" : ""}</td>
      <td>
        <div className="row">
          <select value={target} onChange={(e) => setTarget(e.target.value)}>
            <option value="">Узел назначения</option>
            {choices.map((node) => <option key={node.id} value={node.id}>{node.name}</option>)}
          </select>
          <button disabled={!target} onClick={async () => {
            try {
              await api(`/api/admin/spaces/${space.id}/move`, { method: "POST", body: JSON.stringify({ nodeId: target }) });
              await onChanged();
            } catch (err) {
              onError(err instanceof Error ? err.message : "Ошибка");
            }
          }}>Перенести</button>
        </div>
      </td>
    </tr>
  );
}
