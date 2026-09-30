import { Fragment, useEffect, useState, type FormEvent } from "react";
import { useLocation, useNavigate } from "react-router-dom";
import { api, privilegeLabel } from "./api";
import { subscribeLive } from "./live";
import { GroupMap } from "./map";
import { SetupHint } from "./setup-guide";

type TunnelMark = { spaceId: string; name: string; privileges: string[] };
type SpaceChoice = { id: string; name: string; privileges: string[] };
type Grant = { id: string; email: string; name: string; tunnels: TunnelMark[] };
type Role = { id: string; name: string; privileges: string[] };
type Live = {
  publicKey: string;
  name: string;
  lastSeen: string;
  clock?: string | null;
  latitude?: number | null;
  longitude?: number | null;
  locationFromAdvert?: boolean;
  advertType?: number | null;
  advertName?: string | null;
  advertAt?: string | null;
  frequencyHz?: number | null;
  bandwidthHz?: number | null;
  spreadingFactor?: number | null;
  codingRate?: number | null;
  txDbm?: number | null;
  antennaCm?: number | null;
  forwarding?: boolean | null;
  packetsPublished?: number | null;
  packetsInbound?: number | null;
  duplicates?: number | null;
  publishErrors?: number | null;
  noiseFloor?: number | null;
  txAirSecs?: number | null;
  rxAirSecs?: number | null;
  uptimeSecs?: number | null;
  txQueue?: number | null;
  batteryMv?: number | null;
  tempCx10?: number | null;
  firmware?: string | null;
};
type Device = { id: string; name: string; createdAt?: string; live?: Live | null };
type Activity = { topic: string; messagesPerMinute: number; lastSeen: string };
type FeedItem = { topic: string; seen: string; publicKey: string; name: string; summary: string };
type Login = { id: string; name: string; config: string };
type MapPoint = { publicKey: string; name: string; latitude: number; longitude: number; repeater: boolean };
type MapEdge = { from: string; to: string; seen: string };
type SpaceNode = { id: string; name: string; privileges: string[]; devices: Device[]; activity: Activity[]; feed?: FeedItem[]; map?: { nodes: MapPoint[]; links: MapEdge[] } };
type GroupNode = { id: string; name: string; owner: string; ownerEmail: string; createdAt: string; mine: boolean; privileges: string[]; spaces: SpaceNode[]; spaceChoices: SpaceChoice[]; grants: Grant[] };

const EMPTY = "—";

type Place = { groupId: string | null; spaceId: string | null; deviceId: string | null };

function readPlace(pathname: string): Place {
  const match = /^\/app(?:\/groups\/([^/]+)(?:\/spaces\/([^/]+)(?:\/devices\/([^/]+))?)?)?\/?$/.exec(pathname);
  if (!match) return { groupId: null, spaceId: null, deviceId: null };
  return { groupId: decodePart(match[1]), spaceId: decodePart(match[2]), deviceId: decodePart(match[3]) };
}

function decodePart(value: string | undefined): string | null {
  if (!value) return null;
  try { return decodeURIComponent(value); } catch { return value; }
}

function placePath(place: Place) {
  if (!place.groupId) return "/app";
  let path = `/app/groups/${encodeURIComponent(place.groupId)}`;
  if (!place.spaceId) return path;
  path += `/spaces/${encodeURIComponent(place.spaceId)}`;
  if (!place.deviceId) return path;
  return `${path}/devices/${encodeURIComponent(place.deviceId)}`;
}

function resolvePlace(groups: GroupNode[], wanted: Place): Place {
  const group = groups.find((item) => item.id === wanted.groupId) ?? groups[0] ?? null;
  const space = group && wanted.groupId === group.id && wanted.spaceId
    ? group.spaces.find((item) => item.id === wanted.spaceId) ?? null
    : null;
  const device = space && wanted.deviceId ? space.devices.find((item) => item.id === wanted.deviceId) ?? null : null;
  return { groupId: group?.id ?? null, spaceId: space?.id ?? null, deviceId: device?.id ?? null };
}

function pathsMatch(left: string, right: string) {
  try { return decodeURI(left) === decodeURI(right); } catch { return left === right; }
}

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
  const navigate = useNavigate();
  const location = useLocation();
  const [groups, setGroups] = useState<GroupNode[]>([]);
  const [ready, setReady] = useState(false);
  const [secrets, setSecrets] = useState<Record<string, Login>>({});
  const [name, setName] = useState("");
  const [addingGroup, setAddingGroup] = useState(false);
  const [addingDevice, setAddingDevice] = useState(false);
  const [renaming, setRenaming] = useState(false);
  const [confirmSpace, setConfirmSpace] = useState(false);
  const [error, setError] = useState("");

  const requested = readPlace(location.pathname);
  const place = ready ? resolvePlace(groups, requested) : requested;
  const placeKey = `${requested.groupId ?? ""}\n${requested.spaceId ?? ""}`;
  const [seenPlace, setSeenPlace] = useState(placeKey);
  if (seenPlace !== placeKey) {
    setSeenPlace(placeKey);
    setRenaming(false);
    setAddingDevice(false);
    setConfirmSpace(false);
  }

  function go(next: Place, messagesOn = false) {
    const pathname = placePath(next);
    const search = messagesOn && next.spaceId ? "?messages=1" : "";
    if (pathsMatch(location.pathname, pathname) && location.search === search) return;
    navigate({ pathname, search });
  }

  function takeTree(tree: GroupNode[]) {
    setGroups(tree);
    setReady(true);
  }

  async function reload() {
    takeTree(await api<GroupNode[]>("/api/tree"));
  }

  useEffect(() => {
    reload().catch((err: unknown) => {
      setError(err instanceof Error ? err.message : "Не удалось загрузить");
      setReady(true);
    });
  }, []);
  useEffect(() => subscribeLive(["tree"], { onTree: (tree) => takeTree(tree as GroupNode[]) }), []);
  useEffect(() => {
    if (!ready) return;
    const current = readPlace(location.pathname);
    const resolved = resolvePlace(groups, current);
    const pathname = placePath(resolved);
    const params = new URLSearchParams(location.search);
    const search = resolved.spaceId && params.get("messages") === "1"
      ? "?messages=1"
      : !resolved.spaceId && params.get("map") === "1"
        ? "?map=1"
        : "";
    if (pathsMatch(location.pathname, pathname) && location.search === search) return;
    navigate({ pathname, search }, { replace: true });
  }, [ready, groups, location.pathname, location.search, navigate]);

  function openSpace(nextGroupId: string, nextSpaceId: string) {
    const nextGroup = groups.find((item) => item.id === nextGroupId);
    const nextSpace = nextGroup?.spaces.find((item) => item.id === nextSpaceId);
    go({ groupId: nextGroupId, spaceId: nextSpaceId, deviceId: nextSpace?.devices[0]?.id ?? null });
  }

  function openGroup(id: string) {
    go({ groupId: id, spaceId: null, deviceId: null });
  }

  async function createGroup(event: FormEvent) {
    event.preventDefault();
    setError("");
    try {
      const created = await api<{ id: string }>("/api/groups", { method: "POST", body: JSON.stringify({ name }) });
      setName("");
      setAddingGroup(false);
      await reload();
      go({ groupId: created.id, spaceId: null, deviceId: null });
    } catch (err) {
      setError(err instanceof Error ? err.message : "Не удалось создать группу");
    }
  }

  const group = groups.find((item) => item.id === place.groupId) ?? null;
  const space = group?.spaces.find((item) => item.id === place.spaceId) ?? null;
  const device = space?.devices.find((item) => item.id === place.deviceId) ?? null;
  const messages = !!space && new URLSearchParams(location.search).get("messages") === "1";
  const map = !!group && !space && new URLSearchParams(location.search).get("map") === "1";
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
            {!ready && <p className="muted">Загрузка…</p>}
            {ready && groups.length === 0 && <p className="muted">Групп пока нет.</p>}
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
          {!ready && <div className="scene-body"><p className="muted">Загрузка…</p></div>}
          {ready && !group && <div className="scene-body"><p className="muted">Создайте группу слева.</p></div>}
          {ready && group && !space && (
            <GroupCard
              group={group}
              map={map}
              renaming={renaming}
              onRename={() => setRenaming((open) => !open)}
              onRenamed={async () => { setRenaming(false); await reload(); }}
              onCancelRename={() => setRenaming(false)}
              onDeleted={async () => { await reload(); }}
              onChanged={() => reload()}
              onSpaceCreated={async (id) => { await reload(); go({ groupId: group.id, spaceId: id, deviceId: null }); }}
              onShowMap={() => navigate({ pathname: placePath({ groupId: group.id, spaceId: null, deviceId: null }), search: "?map=1" })}
              onShowDetails={() => navigate({ pathname: placePath({ groupId: group.id, spaceId: null, deviceId: null }), search: "" })}
              onOpenDevice={(spaceId, deviceId) => go({ groupId: group.id, spaceId, deviceId })}
              onError={setError}
            />
          )}
          {ready && group && space && (
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
                      <button type="button" className="ghost" onClick={() => go({ groupId: group.id, spaceId: space.id, deviceId: device?.id ?? null }, !messages)}>Сообщения</button>
                    )}
                    {canManageSpace && (
                      <button type="button" className="ghost danger" onClick={() => setConfirmSpace(true)}>Удалить</button>
                    )}
                  </div>
                </div>
                {canManageSpace && renaming && (
                  <RenameSpace
                    space={space}
                    onSaved={async () => { setRenaming(false); await reload(); }}
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
                        await reload();
                        go({ groupId: group.id, spaceId: space.id, deviceId: created.id }, messages);
                      }}
                      onError={setError}
                    />
                  </div>
                )}
                {messages && space.privileges.includes("view") && (
                  <div className="drawer">
                    <h2>Сообщения</h2>
                    <MessageFeed rows={space.feed ?? []} />
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
                          onClick={() => go({ groupId: group.id, spaceId: space.id, deviceId: item.id }, messages)}
                        >
                          <RepeaterFace name={item.name} live={item.live} createdAt={item.createdAt} />
                        </button>
                      ))}
                    </div>
                  )}
                </section>
                {device && (space.privileges.includes("credentials") || space.privileges.includes("view")) && (
                  <RepeaterSheet
                    title={device.name}
                    live={device.live}
                    secret={space.privileges.includes("credentials") ? secrets[device.id] : undefined}
                    onDelete={space.privileges.includes("credentials") ? async () => {
                      try {
                        await api(`/api/devices/${device.id}`, { method: "DELETE" });
                        setSecrets((current) => {
                          const next = { ...current };
                          delete next[device.id];
                          return next;
                        });
                        await reload();
                      } catch (err) {
                        setError(err instanceof Error ? err.message : "Не удалось удалить репитер");
                        throw err;
                      }
                    } : undefined}
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
              await reload();
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

function GroupCard({ group, map, renaming, onRename, onRenamed, onCancelRename, onDeleted, onChanged, onSpaceCreated, onShowMap, onShowDetails, onOpenDevice, onError }: {
  group: GroupNode;
  map: boolean;
  renaming: boolean;
  onRename: () => void;
  onRenamed: () => Promise<void>;
  onCancelRename: () => void;
  onDeleted: () => Promise<void>;
  onChanged: () => Promise<void>;
  onSpaceCreated: (id: string) => Promise<void>;
  onShowMap: () => void;
  onShowDetails: () => void;
  onOpenDevice: (spaceId: string, deviceId: string) => void;
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
            <button type="button" className={map ? "secondary" : ""} onClick={onShowDetails}>Сведения</button>
            <button type="button" className={map ? "" : "secondary"} onClick={onShowMap}>Карта</button>
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
      <div className={map ? "scene-body map-on" : "scene-body"}>
        {map ? <GroupMap group={group} onOpenDevice={onOpenDevice} /> : (
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
        )}
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

function RepeaterFace({ name, live, createdAt }: { name: string; live?: Live | null; createdAt?: string }) {
  const coords = place(live?.latitude, live?.longitude);
  const advert = live?.advertType != null ? advertLabel(live.advertType) : null;
  return (
    <>
      <span className="repeater-top">
        <strong title={name}>{name}</strong>
        <span className="muted">{advert ?? (live ? "На связи" : "Нет объявления")}</span>
      </span>
      <span className="muted">{coords ? `${live?.locationFromAdvert ? "объявление" : "координаты"} ${coords}` : "Координат нет"}</span>
      <span className="muted">{cardStatus(live, createdAt)}</span>
    </>
  );
}

function RepeaterSheet({ title, live, secret, onDelete }: {
  title: string;
  live?: Live | null;
  secret?: Login;
  onDelete?: () => Promise<void>;
}) {
  const [confirm, setConfirm] = useState(false);
  const coords = place(live?.latitude, live?.longitude);
  const advert = live?.advertType != null ? `${advertLabel(live.advertType)} (${live.advertType})` : EMPTY;
  return (
    <article className="sheet">
      <div className="sheet-head">
        <div>
          <h2>{live?.advertName || live?.name || title}</h2>
          <p className="muted">{live ? `на связи ${new Date(live.lastSeen).toLocaleString()}${live.clock ? ` · часы ${new Date(live.clock).toLocaleString()}` : ""}` : "Нет объявления · статистика ещё не приходила"}</p>
        </div>
        {onDelete && (
          <div className="row actions">
            <button type="button" className="ghost danger" onClick={() => setConfirm(true)}>Удалить репитер</button>
          </div>
        )}
      </div>
      {confirm && (
        <ConfirmDialog
          title="Удалить репитер"
          text={`Репитер «${live?.advertName || live?.name || title}» будет удалён и отключится от тунеля.`}
          confirmLabel="Удалить"
          onClose={() => setConfirm(false)}
          onConfirm={async () => {
            await onDelete?.();
            setConfirm(false);
          }}
        />
      )}
      {secret && <ConfigCard login={secret} />}
      {!secret && onDelete && <p className="muted">Строка настройки показывается один раз, сразу после добавления репитера.</p>}
      <div className="stats">
        <Stat label={live?.locationFromAdvert ? "Координаты объявления" : "Координаты"} value={coords ?? "нет"} />
        <Stat label="Батарея" value={battery(live?.batteryMv)} />
        <Stat label="Температура" value={temperature(live?.tempCx10)} />
        <Stat label="Шум, дБм" value={noiseFloor(live?.noiseFloor)} />
        <Stat label="Эфир TX / RX" value={airtime(live?.txAirSecs, live?.rxAirSecs)} />
      </div>
      <h2>Объявление</h2>
      <Fields rows={[
        ["Тип", advert],
        ["Имя", live?.advertName || live?.name || title],
        ["Публичный ключ", live?.publicKey || EMPTY],
        ["Широта", live?.latitude == null ? EMPTY : live.latitude.toFixed(6)],
        ["Долгота", live?.longitude == null ? EMPTY : live.longitude.toFixed(6)],
        ["Последнее объявление", live?.advertAt ? new Date(live.advertAt).toLocaleString() : EMPTY]
      ]} />
      <h2>Радио и статистика</h2>
      <Fields rows={[
        ["Частота", mhz(live?.frequencyHz)],
        ["Полоса", khz(live?.bandwidthHz)],
        ["Spreading factor", live?.spreadingFactor == null ? EMPTY : String(live.spreadingFactor)],
        ["Coding rate", live?.codingRate == null ? EMPTY : String(live.codingRate)],
        ["Мощность", live?.txDbm == null ? EMPTY : `${live.txDbm} дБм`],
        ["Антенна", live?.antennaCm == null ? EMPTY : `${(live.antennaCm / 100).toLocaleString("ru-RU", { maximumFractionDigits: 2 })} м`],
        ["Пересылка", live?.forwarding == null ? EMPTY : live.forwarding ? "включена" : "выключена"],
        ["Очередь передачи", count(live?.txQueue)],
        ["Батарея", battery(live?.batteryMv)],
        ["Температура", temperature(live?.tempCx10)],
        ["Аптайм", uptime(live?.uptimeSecs)],
        ["Прошивка", firmware(live?.firmware)],
        ["Принято / отдано", live?.packetsInbound == null && live?.packetsPublished == null ? EMPTY : `${live?.packetsInbound ?? 0} / ${live?.packetsPublished ?? 0}`],
        ["Дубли / ошибки публикации", live?.duplicates == null && live?.publishErrors == null ? EMPTY : `${live?.duplicates ?? 0} / ${live?.publishErrors ?? 0}`]
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
  const [copyError, setCopyError] = useState("");
  const titled = login.name && login.name !== "Репитер" ? ` «${login.name}»` : "";
  return (
    <div className="secret">
      <p className="muted">Строка для веб-интерфейса репитера{titled}: адрес, логин, пароль и сертификат. Вставьте её один раз — повторно пароль не показывается.</p>
      <pre className="cli">{login.config}</pre>
      <div className="row">
        <button type="button" onClick={() => {
          setCopyError("");
          copyText(login.config).then(() => {
            setDone(true);
            window.setTimeout(() => setDone(false), 1200);
          }).catch(() => setCopyError("Не удалось скопировать строку"));
        }}>{done ? "Скопировано" : "Копировать настройку"}</button>
        <SetupHint />
      </div>
      {copyError && <p className="error">{copyError}</p>}
    </div>
  );
}

function copyText(value: string) {
  if (window.isSecureContext && navigator.clipboard?.writeText)
    return navigator.clipboard.writeText(value).catch(() => copyWithSelection(value));
  try {
    copyWithSelection(value);
  } catch (err) {
    return Promise.reject(err);
  }
  return Promise.resolve();
}

function copyWithSelection(value: string) {
  const area = document.createElement("textarea");
  area.value = value;
  area.setAttribute("readonly", "");
  area.style.position = "fixed";
  area.style.top = "0";
  area.style.left = "0";
  document.body.appendChild(area);
  area.focus();
  area.select();
  area.setSelectionRange(0, value.length);
  const copied = document.execCommand("copy");
  document.body.removeChild(area);
  if (!copied) throw new Error("Не удалось скопировать");
}

function MessageFeed({ rows }: { rows: FeedItem[] }) {
  if (!rows.length) return <p className="muted">Сообщений пока нет. Они появятся, когда репитер выйдет на связь.</p>;
  return (
    <table>
      <thead><tr><th>Время</th><th>Репитер</th><th>Ключ</th><th>Сообщение</th></tr></thead>
      <tbody>
        {rows.map((row, index) => (
          <tr key={`${row.seen}-${row.publicKey}-${index}`}>
            <td>{new Date(row.seen).toLocaleString()}</td>
            <td>{row.name || EMPTY}</td>
            <td title={row.publicKey}>{shortKey(row.publicKey)}</td>
            <td>{row.summary}</td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

function advertLabel(type: number) {
  return ["узел", "чат", "репитер", "комната", "датчик"][type] ?? String(type);
}

function place(latitude?: number | null, longitude?: number | null) {
  if (latitude == null || longitude == null) return null;
  return `${latitude.toFixed(6)}, ${longitude.toFixed(6)}`;
}

function mhz(hz?: number | null) {
  if (hz == null) return EMPTY;
  return `${(hz / 1e6).toLocaleString("ru-RU", { maximumFractionDigits: 3 })} МГц`;
}

function khz(hz?: number | null) {
  if (hz == null) return EMPTY;
  return `${(hz / 1e3).toLocaleString("ru-RU", { maximumFractionDigits: 1 })} кГц`;
}

function cardStatus(live?: Live | null, createdAt?: string) {
  if (!live) return createdAt ? `создан ${new Date(createdAt).toLocaleString()}` : "батарея неизвестна";
  const charge = live.batteryMv == null ? "" : battery(live.batteryMv);
  const heat = live.tempCx10 == null ? "" : temperature(live.tempCx10);
  const sensed = [charge, heat].filter((item) => item && item !== EMPTY).join(" · ");
  const seen = `на связи ${new Date(live.lastSeen).toLocaleString()}`;
  return sensed ? `${sensed} · ${seen}` : seen;
}

function battery(mv?: number | null) {
  if (mv == null) return EMPTY;
  if (mv === 0) return "не измерена";
  return `${(mv / 1000).toLocaleString("ru-RU", { minimumFractionDigits: 2, maximumFractionDigits: 2 })} В`;
}

function temperature(cx10?: number | null) {
  if (cx10 == null) return EMPTY;
  if (cx10 === -32768) return "не ответил";
  return `${(cx10 / 10).toLocaleString("ru-RU", { minimumFractionDigits: 1, maximumFractionDigits: 1 })} °C`;
}

function noiseFloor(value?: number | null) {
  if (value == null) return EMPTY;
  if (value === 0) return "не измерен";
  return String(value);
}

function airtime(tx?: number | null, rx?: number | null) {
  if (tx == null && rx == null) return EMPTY;
  return `${tx ?? 0} / ${rx ?? 0} с`;
}

function uptime(seconds?: number | null) {
  if (seconds == null) return EMPTY;
  const total = Math.max(0, Math.floor(seconds));
  const days = Math.floor(total / 86400);
  const hours = Math.floor((total % 86400) / 3600);
  const minutes = Math.floor((total % 3600) / 60);
  const rest = total % 60;
  if (days > 0) return hours > 0 ? `${days} д ${hours} ч` : `${days} д`;
  if (hours > 0) return minutes > 0 ? `${hours} ч ${minutes} мин` : `${hours} ч`;
  if (minutes > 0) return rest > 0 ? `${minutes} мин ${rest} с` : `${minutes} мин`;
  return `${rest} с`;
}

function count(value?: number | null) {
  if (value == null) return EMPTY;
  return String(value);
}

function firmware(value?: string | null) {
  const text = value?.trim() ?? "";
  return text.length > 0 ? text : EMPTY;
}

function shortKey(key: string) {
  if (key.length < 12) return key || EMPTY;
  return `${key.slice(0, 8)}…${key.slice(-4)}`;
}

function DeviceForm({ spaceId, privileges, onCreated, onError }: { spaceId: string; privileges: string[]; onCreated: (value: Login) => Promise<void>; onError: (value: string) => void }) {
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
          body: JSON.stringify({ canSubscribe, canPublish })
        });
        await onCreated(created);
      } catch (err) {
        onError(err instanceof Error ? err.message : "Не удалось выдать логин");
      }
    }}>
      <p className="muted">Имя подставится из объявления репитера, когда он выйдет на связь.</p>
      <div className="composer">
        <button type="submit">Добавить</button>
      </div>
      <SetupHint />
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
