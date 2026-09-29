import { Fragment, useEffect, useState, type FormEvent } from "react";
import { api, privilegeLabel } from "./api";

type TunnelMark = { spaceId: string; name: string; privileges: string[] };
type SpaceChoice = { id: string; name: string; privileges: string[] };
type Grant = { id: string; email: string; name: string; tunnels: TunnelMark[] };
type Role = { id: string; name: string; privileges: string[] };
type Device = { id: string; name: string; createdAt?: string };
type Activity = { topic: string; messagesPerMinute: number; lastSeen: string };
type Login = { id: string; name: string; config: string };
type SpaceNode = { id: string; name: string; privileges: string[]; devices: Device[]; activity: Activity[] };
type GroupNode = { id: string; name: string; owner: string; ownerEmail: string; createdAt: string; mine: boolean; privileges: string[]; spaces: SpaceNode[]; spaceChoices: SpaceChoice[]; grants: Grant[] };

const EMPTY = "—";

export function PanelBar({ email }: { email?: string }) {
  return (
    <header className="topbar">
      <div className="brand">
        <img className="brand-mark" src="/icon.svg" alt="" />
        <strong>MeshCore</strong>
      </div>
      {email && <span className="session-email" title={email}>{email}</span>}
    </header>
  );
}

export function GroupsPage({ email, onLogout }: { email: string; onLogout: () => void }) {
  const [groups, setGroups] = useState<GroupNode[]>([]);
  const [groupId, setGroupId] = useState<string | null>(null);
  const [spaceId, setSpaceId] = useState<string | null>(null);
  const [deviceId, setDeviceId] = useState<string | null>(null);
  const [secrets, setSecrets] = useState<Record<string, Login>>({});
  const [name, setName] = useState("");
  const [addingGroup, setAddingGroup] = useState(false);
  const [addingDevice, setAddingDevice] = useState(false);
  const [renaming, setRenaming] = useState(false);
  const [messages, setMessages] = useState(false);
  const [confirmSpace, setConfirmSpace] = useState(false);
  const [error, setError] = useState("");

  async function load(nextGroupId?: string | null, nextSpaceId?: string | null, nextDeviceId?: string | null) {
    const tree = await api<GroupNode[]>("/api/tree");
    setGroups(tree);
    const group = tree.find((item) => item.id === (nextGroupId === undefined ? groupId : nextGroupId)) ?? (nextGroupId ? null : tree[0]) ?? null;
    setGroupId(group?.id ?? null);
    const wantedSpace = nextSpaceId === undefined ? spaceId : nextSpaceId;
    const space = wantedSpace ? group?.spaces.find((item) => item.id === wantedSpace) ?? null : null;
    setSpaceId(space?.id ?? null);
    const wantedDevice = nextDeviceId === undefined ? deviceId : nextDeviceId;
    const device = wantedDevice ? space?.devices.find((item) => item.id === wantedDevice) ?? null : null;
    setDeviceId(device?.id ?? null);
  }
  useEffect(() => { load().catch((err) => setError(err.message)); }, []);

  function openSpace(nextGroupId: string, nextSpaceId: string) {
    const group = groups.find((item) => item.id === nextGroupId);
    const space = group?.spaces.find((item) => item.id === nextSpaceId);
    setGroupId(nextGroupId);
    setSpaceId(nextSpaceId);
    setDeviceId(space?.devices[0]?.id ?? null);
    setRenaming(false);
    setAddingDevice(false);
    setMessages(false);
    setConfirmSpace(false);
  }

  function openGroup(id: string) {
    setGroupId(id);
    setSpaceId(null);
    setDeviceId(null);
    setRenaming(false);
    setAddingDevice(false);
    setMessages(false);
    setConfirmSpace(false);
  }

  async function createGroup(event: FormEvent) {
    event.preventDefault();
    setError("");
    try {
      const created = await api<{ id: string }>("/api/groups", { method: "POST", body: JSON.stringify({ name }) });
      setName("");
      setAddingGroup(false);
      await load(created.id, null, null);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Не удалось создать группу");
    }
  }

  const group = groups.find((item) => item.id === groupId) ?? null;
  const space = group?.spaces.find((item) => item.id === spaceId) ?? null;
  const device = space?.devices.find((item) => item.id === deviceId) ?? null;
  const secret = device ? secrets[device.id] : undefined;
  const canManageSpace = !!space?.privileges.includes("access");

  return (
    <>
      {error && <p className="error">{error}</p>}
      <div className="stage">
        <PanelBar email={email} />
        <div className="stage-body">
        <aside className="tree">
          <div className="tree-head">
            <div className="tree-label">
              <p className="kicker">Сеть</p>
              <button type="button" className="ghost" onClick={onLogout}>Выйти</button>
            </div>
            <button type="button" className="secondary" onClick={() => { setName(""); setError(""); setAddingGroup(true); }}>
              Новая группа
            </button>
          </div>
          <div className="tree-scroll">
            {groups.length === 0 && <p className="muted">Групп пока нет.</p>}
            {groups.map((item) => (
              <div key={item.id}>
                <div className="tree-group-row">
                  <button type="button" className={item.id === group?.id && !space ? "tree-group selected" : "tree-group"} onClick={() => openGroup(item.id)}>
                    {item.name}
                  </button>
                </div>
                {item.spaces.map((child) => (
                  <button
                    key={child.id}
                    type="button"
                    className={child.id === space?.id ? "pick tree-space selected" : "pick tree-space"}
                    onClick={() => openSpace(item.id, child.id)}
                  >
                    <strong>{child.name}</strong>
                    <span className="count">{child.devices.length}</span>
                  </button>
                ))}
              </div>
            ))}
          </div>
        </aside>
        <section className="scene">
          {!group && <div className="scene-body"><p className="muted">Создайте группу слева.</p></div>}
          {group && !space && (
            <GroupCard
              group={group}
              renaming={renaming}
              onRename={() => setRenaming((open) => !open)}
              onRenamed={async () => { setRenaming(false); await load(group.id, null, null); }}
              onCancelRename={() => setRenaming(false)}
              onDeleted={async () => { await load(null, null, null); }}
              onChanged={() => load(group.id, null, null)}
              onSpaceCreated={async (id) => { await load(group.id, id, null); }}
              onError={setError}
            />
          )}
          {group && space && (
            <>
              <header className="scene-head">
                <div className="scene-title">
                  <h2>{group.name} / {space.name}{group.mine ? "" : ` · ${group.owner}`}</h2>
                  <div className="row actions">
                    {space.privileges.includes("credentials") && (
                      <button type="button" onClick={() => setAddingDevice((open) => !open)}>{addingDevice ? "Закрыть" : "Добавить репитер"}</button>
                    )}
                    {canManageSpace && (
                      <button type="button" className="secondary" onClick={() => setRenaming((open) => !open)}>Переименовать</button>
                    )}
                    {space.privileges.includes("view") && (
                      <button type="button" className="ghost" onClick={() => setMessages((open) => !open)}>Сообщения</button>
                    )}
                    {canManageSpace && (
                      <button type="button" className="ghost danger" onClick={() => setConfirmSpace(true)}>Удалить</button>
                    )}
                  </div>
                </div>
                {canManageSpace && renaming && (
                  <RenameSpace
                    space={space}
                    onSaved={async () => { setRenaming(false); await load(group.id, space.id); }}
                    onCancel={() => setRenaming(false)}
                    onError={setError}
                  />
                )}
              </header>
              <div className="scene-body">
                {addingDevice && space.privileges.includes("credentials") && (
                  <div className="drawer">
                    <DeviceForm
                      spaceId={space.id}
                      privileges={space.privileges}
                      onCreated={async (created) => {
                        setSecrets((current) => ({ ...current, [created.id]: created }));
                        setAddingDevice(false);
                        await load(group.id, space.id, created.id);
                      }}
                      onError={setError}
                    />
                  </div>
                )}
                {messages && space.privileges.includes("view") && (
                  <div className="drawer">
                    <h2>Сообщения</h2>
                    <ActivityTable rows={space.activity} />
                  </div>
                )}
                <section className="sheet repeater-board">
                  <h2>Репитеры</h2>
                  {space.privileges.includes("credentials") && space.devices.length === 0 && (
                    <p className="muted">В этом тунеле репитеров ещё нет. «Добавить репитер» выдаст строку настройки.</p>
                  )}
                  {space.devices.length > 0 && (
                    <div className="repeater-grid">
                      {space.devices.map((item) => (
                        <button
                          key={item.id}
                          type="button"
                          className={item.id === device?.id ? "repeater selected" : "repeater"}
                          onClick={() => setDeviceId(item.id)}
                        >
                          <span className="repeater-top">
                            <strong>{item.name}</strong>
                            <span className="muted">Нет объявления</span>
                          </span>
                          <span className="muted">Координат в объявлении нет</span>
                          <span className="muted">{item.createdAt ? `создан ${new Date(item.createdAt).toLocaleString()}` : "батарея неизвестна"}</span>
                        </button>
                      ))}
                    </div>
                  )}
                </section>
                {device && space.privileges.includes("credentials") && (
                  <RepeaterSheet
                    device={device}
                    secret={secret}
                    onDelete={async () => {
                      try {
                        await api(`/api/devices/${device.id}`, { method: "DELETE" });
                        setSecrets((current) => {
                          const next = { ...current };
                          delete next[device.id];
                          return next;
                        });
                        await load(group.id, space.id, null);
                      } catch (err) {
                        setError(err instanceof Error ? err.message : "Не удалось удалить репитер");
                        throw err;
                      }
                    }}
                  />
                )}
              </div>
            </>
          )}
        </section>
        </div>
        <footer className="bottombar">Подключение репитеров и доступ к ним.</footer>
      </div>
      {addingGroup && (
        <div className="modal-backdrop" onClick={() => setAddingGroup(false)}>
          <form className="modal-card" role="dialog" aria-modal="true" aria-labelledby="new-group-title" onSubmit={createGroup} onClick={(event) => event.stopPropagation()} onKeyDown={(event) => { if (event.key === "Escape") setAddingGroup(false); }}>
            <h2 id="new-group-title">Новая группа</h2>
            <label>Название
              <input value={name} onChange={(e) => setName(e.target.value)} required autoFocus />
            </label>
            <div className="row">
              <button type="submit">Создать группу</button>
              <button type="button" className="secondary" onClick={() => setAddingGroup(false)}>Отмена</button>
            </div>
          </form>
        </div>
      )}
      {confirmSpace && space && group && (
        <ConfirmDialog
          title="Удалить тунель"
          text={`Тунель «${space.name}» будет удалён вместе с репитерами.`}
          confirmLabel="Удалить"
          onClose={() => setConfirmSpace(false)}
          onConfirm={async () => {
            try {
              await api(`/api/spaces/${space.id}`, { method: "DELETE" });
              setConfirmSpace(false);
              await load(group.id, null, null);
            } catch (err) {
              setError(err instanceof Error ? err.message : "Не удалось удалить тунель");
            }
          }}
        />
      )}
    </>
  );
}

export function ConfirmDialog({ title, text, confirmLabel, onConfirm, onClose }: {
  title: string;
  text: string;
  confirmLabel: string;
  onConfirm: () => Promise<void>;
  onClose: () => void;
}) {
  return (
    <div className="modal-backdrop" onClick={onClose}>
      <div className="modal-card" role="dialog" aria-modal="true" aria-labelledby="confirm-title" onClick={(event) => event.stopPropagation()} onKeyDown={(event) => { if (event.key === "Escape") onClose(); }}>
        <h2 id="confirm-title">{title}</h2>
        <p>{text}</p>
        <div className="row">
          <button type="button" className="secondary" autoFocus onClick={onClose}>Отмена</button>
          <button type="button" className="danger" onClick={() => onConfirm().catch(() => undefined)}>{confirmLabel}</button>
        </div>
      </div>
    </div>
  );
}

function CreateSpace({ groupId, onCreated, onError }: { groupId: string; onCreated: (id: string) => Promise<void>; onError: (value: string) => void }) {
  const [name, setName] = useState("");
  return (
    <form className="composer" onSubmit={async (event) => {
      event.preventDefault();
      try {
        const created = await api<{ id: string }>(`/api/groups/${groupId}/spaces`, { method: "POST", body: JSON.stringify({ name }) });
        setName("");
        await onCreated(created.id);
      } catch (err) {
        onError(err instanceof Error ? err.message : "Не удалось создать тунель");
      }
    }}>
      <input placeholder="Имя тунеля" value={name} onChange={(e) => setName(e.target.value)} required />
      <button type="submit">Создать</button>
    </form>
  );
}

function GroupCard({ group, renaming, onRename, onRenamed, onCancelRename, onDeleted, onChanged, onSpaceCreated, onError }: {
  group: GroupNode;
  renaming: boolean;
  onRename: () => void;
  onRenamed: () => Promise<void>;
  onCancelRename: () => void;
  onDeleted: () => Promise<void>;
  onChanged: () => Promise<void>;
  onSpaceCreated: (id: string) => Promise<void>;
  onError: (value: string) => void;
}) {
  const [addingSpace, setAddingSpace] = useState(false);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const tunnels = group.spaces.length;
  const repeaters = group.spaces.reduce((sum, item) => sum + item.devices.length, 0);
  const canManage = group.privileges.includes("access");
  useEffect(() => { setAddingSpace(false); setConfirmDelete(false); }, [group.id]);
  return (
    <>
      <header className="scene-head">
        <p className="kicker">Группа</p>
        <div className="scene-title">
          <h2>{group.name}</h2>
          <div className="row actions">
            {canManage && <button type="button" onClick={() => setAddingSpace((open) => !open)}>{addingSpace ? "Закрыть" : "Новый тунель"}</button>}
            {canManage && <button type="button" className="secondary" onClick={onRename}>{renaming ? "Закрыть" : "Переименовать"}</button>}
            {group.mine && (
              <button type="button" className="ghost danger" onClick={() => setConfirmDelete(true)}>Удалить</button>
            )}
          </div>
        </div>
        {canManage && addingSpace && (
          <CreateSpace groupId={group.id} onCreated={async (id) => { setAddingSpace(false); await onSpaceCreated(id); }} onError={onError} />
        )}
        {canManage && renaming && <GroupRename group={group} onChanged={onRenamed} onCancel={onCancelRename} onError={onError} />}
        {confirmDelete && (
          <ConfirmDialog
            title="Удалить группу"
            text={`Группа «${group.name}» будет удалена вместе с тунелями и репитерами.`}
            confirmLabel="Удалить"
            onClose={() => setConfirmDelete(false)}
            onConfirm={async () => {
              try {
                await api(`/api/groups/${group.id}`, { method: "DELETE" });
                setConfirmDelete(false);
                await onDeleted();
              } catch (err) {
                onError(err instanceof Error ? err.message : "Не удалось удалить группу");
              }
            }}
          />
        )}
      </header>
      <div className="scene-body">
        <article className="sheet">
          <div className="stats">
            <Stat label="Тунели" value={String(tunnels)} />
            <Stat label="Репитеры" value={String(repeaters)} />
            <Stat label="С доступом" value={canManage ? String(group.grants.length) : EMPTY} />
          </div>
          <h2>Сведения</h2>
          <Fields rows={[
            ["Владелец", group.owner],
            ["Почта", group.ownerEmail || EMPTY],
            ["Создана", group.createdAt ? new Date(group.createdAt).toLocaleString() : EMPTY],
            ["Ваш доступ", group.mine ? "Владелец" : group.spaces.map((item) => `${item.name}: ${item.privileges.filter((flag) => flag !== "topics").map(privilegeLabel).join(", ") || EMPTY}`).join("; ") || EMPTY]
          ]} />
          {tunnels === 0 && <p className="muted">В группе ещё нет тунелей.</p>}
          {canManage && (
            <>
              <h2>Доступ</h2>
              <GrantBox
                target={`/api/groups/${group.id}/grants`}
                grants={group.grants}
                spaces={group.spaceChoices ?? []}
                onChanged={onChanged}
                onError={onError}
              />
            </>
          )}
        </article>
      </div>
    </>
  );
}

function RenameSpace({ space, onSaved, onCancel, onError }: { space: SpaceNode; onSaved: () => Promise<void>; onCancel: () => void; onError: (value: string) => void }) {
  const [name, setName] = useState(space.name);
  useEffect(() => setName(space.name), [space.id, space.name]);
  return (
    <form className="composer" onSubmit={async (event) => {
      event.preventDefault();
      try {
        await api(`/api/spaces/${space.id}`, { method: "PATCH", body: JSON.stringify({ name }) });
        await onSaved();
      } catch (err) {
        onError(err instanceof Error ? err.message : "Не удалось переименовать");
      }
    }}>
      <input aria-label="Имя тунеля" value={name} onChange={(e) => setName(e.target.value)} required />
      <button type="submit">Сохранить</button>
      <button className="secondary" type="button" onClick={onCancel}>Отмена</button>
    </form>
  );
}

function GroupRename({ group, onChanged, onCancel, onError }: { group: GroupNode; onChanged: () => Promise<void>; onCancel: () => void; onError: (value: string) => void }) {
  const [name, setName] = useState(group.name);
  useEffect(() => setName(group.name), [group.id, group.name]);
  return (
    <form className="composer" onSubmit={async (event) => {
      event.preventDefault();
      try {
        await api(`/api/groups/${group.id}`, { method: "PATCH", body: JSON.stringify({ name }) });
        await onChanged();
      } catch (err) {
        onError(err instanceof Error ? err.message : "Не удалось переименовать");
      }
    }}>
      <input aria-label="Название группы" value={name} onChange={(e) => setName(e.target.value)} required />
      <button type="submit">Сохранить</button>
      <button className="secondary" type="button" onClick={onCancel}>Отмена</button>
    </form>
  );
}

function RepeaterSheet({ device, secret, onDelete }: {
  device: Device;
  secret?: Login;
  onDelete: () => Promise<void>;
}) {
  const [confirm, setConfirm] = useState(false);
  return (
    <article className="sheet">
      <div className="sheet-head">
        <div>
          <h2>{device.name}</h2>
          <p className="muted">Нет объявления · статистика ещё не приходила</p>
        </div>
        <div className="row actions">
          <button type="button" className="ghost danger" onClick={() => setConfirm(true)}>Удалить репитер</button>
        </div>
      </div>
      {confirm && (
        <ConfirmDialog
          title="Удалить репитер"
          text={`Репитер «${device.name}» будет удалён и отключится от тунеля.`}
          confirmLabel="Удалить"
          onClose={() => setConfirm(false)}
          onConfirm={async () => {
            await onDelete();
            setConfirm(false);
          }}
        />
      )}
      {secret && <ConfigCard login={secret} />}
      {!secret && <p className="muted">Строка настройки показывается один раз, сразу после добавления репитера.</p>}
      <div className="stats">
        <Stat label="Координаты объявления" value="нет" />
        <Stat label="Батарея" value={EMPTY} />
        <Stat label="Шум, дБм" value={EMPTY} />
        <Stat label="Эфир" value={EMPTY} />
      </div>
      <h2>Объявление</h2>
      <Fields rows={[
        ["Тип", "репитер (2)"],
        ["Имя", device.name],
        ["Публичный ключ", EMPTY],
        ["Широта", "0"],
        ["Долгота", "0"],
        ["Последнее объявление", EMPTY]
      ]} />
      <h2>Радио и статистика</h2>
      <Fields rows={[
        ["Частота", EMPTY],
        ["Полоса", EMPTY],
        ["Spreading factor", EMPTY],
        ["Coding rate", EMPTY],
        ["Мощность", EMPTY],
        ["Очередь передачи", EMPTY],
        ["Аптайм", EMPTY],
        ["Прошивка", EMPTY],
        ["Принято / отправлено", EMPTY]
      ]} />
    </article>
  );
}

function Stat({ label, value }: { label: string; value: string }) {
  return <div className="stat"><strong>{value}</strong><span>{label}</span></div>;
}

function Fields({ rows }: { rows: [string, string][] }) {
  return (
    <dl className="fields">
      {rows.map(([label, value]) => (
        <div className="field" key={label}>
          <dt>{label}</dt>
          <dd>{value}</dd>
        </div>
      ))}
    </dl>
  );
}

function ConfigCard({ login }: { login: { name: string; config: string } }) {
  const [done, setDone] = useState(false);
  return (
    <div className="secret">
      <p className="muted">Строка для веб-интерфейса репитера «{login.name}»: адрес, логин, пароль и сертификат. Вставьте её один раз — повторно пароль не показывается.</p>
      <pre className="cli">{login.config}</pre>
      <div className="row">
        <button type="button" onClick={async () => {
          await navigator.clipboard.writeText(login.config);
          setDone(true);
          window.setTimeout(() => setDone(false), 1200);
        }}>{done ? "Скопировано" : "Копировать настройку"}</button>
      </div>
    </div>
  );
}

function ActivityTable({ rows }: { rows: Activity[] }) {
  if (!rows?.length) return <p className="muted">Сообщений пока нет. Они появятся, когда репитер выйдет на связь.</p>;
  return (
    <table>
      <thead><tr><th>Публичный ключ</th><th>В минуту</th><th>Последнее</th></tr></thead>
      <tbody>
        {rows.map((row) => (
          <tr key={row.topic}><td>{row.topic}</td><td>{row.messagesPerMinute}</td><td>{new Date(row.lastSeen).toLocaleString()}</td></tr>
        ))}
      </tbody>
    </table>
  );
}

function DeviceForm({ spaceId, privileges, onCreated, onError }: { spaceId: string; privileges: string[]; onCreated: (value: Login) => Promise<void>; onError: (value: string) => void }) {
  const [name, setName] = useState("");
  const canChooseSubscribe = privileges.includes("subscribe");
  const canChoosePublish = privileges.includes("publish");
  const [canSubscribe, setCanSubscribe] = useState(canChooseSubscribe);
  const [canPublish, setCanPublish] = useState(canChoosePublish);
  const limited = !canChooseSubscribe || !canChoosePublish;
  return (
    <form className="stack" onSubmit={async (event) => {
      event.preventDefault();
      try {
        const created = await api<Login>(`/api/spaces/${spaceId}/devices`, {
          method: "POST",
          body: JSON.stringify({ name, canSubscribe, canPublish })
        });
        setName("");
        await onCreated(created);
      } catch (err) {
        onError(err instanceof Error ? err.message : "Не удалось выдать логин");
      }
    }}>
      <div className="composer">
        <input placeholder="Имя репитера" value={name} onChange={(e) => setName(e.target.value)} required />
        <button type="submit">Добавить</button>
      </div>
      {limited && (
        <div className="row">
          {canChooseSubscribe && <label className="check"><input type="checkbox" checked={canSubscribe} onChange={(e) => setCanSubscribe(e.target.checked)} />Чтение</label>}
          {canChoosePublish && <label className="check"><input type="checkbox" checked={canPublish} onChange={(e) => setCanPublish(e.target.checked)} />Запись</label>}
        </div>
      )}
    </form>
  );
}

function samePrivileges(left: string[], right: string[]) {
  return [...left].sort().join(",") === [...right].sort().join(",");
}

function rightsText(privileges: string[]) {
  return privileges.filter((item) => item !== "topics").map(privilegeLabel).join(", ");
}

function GrantBox({ target, grants, spaces, onChanged, onError }: {
  target: string;
  grants: Grant[];
  spaces: SpaceChoice[];
  onChanged: () => Promise<void>;
  onError: (value: string) => void;
}) {
  const [email, setEmail] = useState("");
  const [openId, setOpenId] = useState<string | null>(null);
  const [closing, setClosing] = useState<Grant | null>(null);

  return (
    <div className="grant">
      <p className="muted">Доступ выдаётся на группу. В деталях отмечается, на какие тунели он действует и с какими правами.</p>
      <form className="composer" onSubmit={async (event) => {
        event.preventDefault();
        try {
          const created = await api<{ id: string }>(target, { method: "POST", body: JSON.stringify({ email }) });
          setEmail("");
          setOpenId(created.id);
          await onChanged();
        } catch (err) {
          onError(err instanceof Error ? err.message : "Не удалось выдать доступ");
        }
      }}>
        <input placeholder="Почта человека" type="email" value={email} onChange={(e) => setEmail(e.target.value)} required />
        <button type="submit">Открыть доступ</button>
      </form>
      {grants.length === 0 && <p className="muted">Пока никому не открыто.</p>}
      {grants.length > 0 && (
        <table>
          <tbody>
            {grants.map((grant) => (
              <Fragment key={grant.id}>
                <tr>
                  <td>{grant.name}<div className="muted">{grant.email}</div></td>
                  <td>{grant.tunnels.length ? grant.tunnels.map((item) => `${item.name}: ${rightsText(item.privileges)}`).join("; ") : "тунели не отмечены"}</td>
                  <td>
                    <div className="row">
                      <button type="button" className="secondary" onClick={() => setOpenId((current) => current === grant.id ? null : grant.id)}>{openId === grant.id ? "Скрыть" : "Подробнее"}</button>
                      <button type="button" className="secondary" onClick={() => setClosing(grant)}>Закрыть</button>
                    </div>
                  </td>
                </tr>
                {openId === grant.id && (
                  <tr>
                    <td colSpan={3}>
                      <GrantDetails
                        grant={grant}
                        spaces={spaces}
                        onChanged={onChanged}
                        onError={onError}
                      />
                    </td>
                  </tr>
                )}
              </Fragment>
            ))}
          </tbody>
        </table>
      )}
      {closing && (
        <ConfirmDialog
          title="Закрыть доступ"
          text={`Доступ для ${closing.name} будет закрыт.`}
          confirmLabel="Закрыть"
          onClose={() => setClosing(null)}
          onConfirm={async () => {
            try {
              await api(`/api/grants/${closing.id}`, { method: "DELETE" });
              setClosing(null);
              if (openId === closing.id) setOpenId(null);
              await onChanged();
            } catch (err) {
              onError(err instanceof Error ? err.message : "Не удалось закрыть доступ");
            }
          }}
        />
      )}
    </div>
  );
}

function GrantDetails({ grant, spaces, onChanged, onError }: {
  grant: Grant;
  spaces: SpaceChoice[];
  onChanged: () => Promise<void>;
  onError: (value: string) => void;
}) {
  const [roles, setRoles] = useState<Role[]>([]);
  const [rows, setRows] = useState<{ id: string; on: boolean; roleId: string }[]>([]);
  useEffect(() => { api<Role[]>("/api/roles").then(setRoles).catch(() => undefined); }, []);
  useEffect(() => {
    setRows(spaces.map((space) => {
      const mark = grant.tunnels.find((item) => item.spaceId === space.id);
      const allowed = roles.filter((role) => role.privileges.every((item) => space.privileges.includes(item)));
      const match = allowed.find((role) => mark && samePrivileges(role.privileges, mark.privileges));
      const preferred = allowed.find((role) => role.name === "Оператор") ?? allowed[0];
      return {
        id: space.id,
        on: !!mark,
        roleId: match?.id ?? (mark ? "current" : preferred?.id ?? "")
      };
    }));
  }, [grant, spaces, roles]);

  if (!spaces.length) return <p className="muted">В группе ещё нет тунелей, отметить нечего.</p>;

  return (
    <form className="tunnel-marks" onSubmit={async (event) => {
      event.preventDefault();
      try {
        const tunnels = rows.flatMap((row) => {
          const space = spaces.find((item) => item.id === row.id);
          if (!row.on || !space?.privileges.includes("access")) return [];
          if (row.roleId === "current") {
            const mark = grant.tunnels.find((item) => item.spaceId === row.id);
            return mark ? [{ spaceId: row.id, privileges: mark.privileges }] : [];
          }
          const role = roles.find((item) => item.id === row.roleId);
          if (!role) return [];
          return [{ spaceId: row.id, privileges: role.privileges }];
        });
        await api(`/api/grants/${grant.id}`, { method: "PUT", body: JSON.stringify({ tunnels }) });
        await onChanged();
      } catch (err) {
        onError(err instanceof Error ? err.message : "Не удалось сохранить доступ");
      }
    }}>
      {spaces.map((space) => {
        const row = rows.find((item) => item.id === space.id);
        const editable = space.privileges.includes("access");
        const allowed = roles.filter((role) => role.privileges.every((item) => space.privileges.includes(item)));
        const mark = grant.tunnels.find((item) => item.spaceId === space.id);
        const selected = roles.find((role) => role.id === row?.roleId);
        const caption = row?.on
          ? (row.roleId === "current" && mark ? rightsText(mark.privileges) : selected ? rightsText(selected.privileges) : "")
          : "";
        return (
          <div className="tunnel-mark" key={space.id}>
            <label className="check">
              <input
                type="checkbox"
                checked={row?.on ?? false}
                disabled={!editable || (!row?.on && allowed.length === 0)}
                onChange={(event) => setRows((current) => current.map((item) => item.id === space.id ? { ...item, on: event.target.checked } : item))}
              />
              <span>{space.name}{caption ? <span className="muted"> · {caption}</span> : null}</span>
            </label>
            {editable ? (
              <select
                aria-label={`Права на ${space.name}`}
                value={row?.roleId ?? ""}
                disabled={!row?.on || (allowed.length === 0 && row?.roleId !== "current")}
                onChange={(event) => setRows((current) => current.map((item) => item.id === space.id ? { ...item, roleId: event.target.value } : item))}
              >
                {row?.roleId === "current" && mark && <option value="current">{rightsText(mark.privileges)}</option>}
                {allowed.map((role) => <option key={role.id} value={role.id}>{role.name}</option>)}
              </select>
            ) : (
              <span className="muted">{mark ? rightsText(mark.privileges) : "нет отметки"}</span>
            )}
          </div>
        );
      })}
      <p className="muted grant-note">Новый тунель сам в этот доступ не входит — его нужно отметить.</p>
      <div><button type="submit">Сохранить</button></div>
    </form>
  );
}
