import { useEffect, useMemo, useRef, useState, type PointerEvent as ReactPointerEvent } from "react";

type Point = { publicKey: string; name: string; latitude: number; longitude: number; repeater: boolean };
type Edge = { from: string; to: string; seen: string };
type Space = {
  id: string;
  name: string;
  privileges: string[];
  devices: { id: string; live?: { publicKey: string } | null }[];
  map?: { nodes: Point[]; links: Edge[]; unplaced?: string[] };
};

const COLORS = ["#38bdf8", "#a78bfa", "#fbbf24", "#fb7185", "#34d399", "#f472b6"];

type Marker = Point & { spaceId: string; spaceName: string; color: string };
type Line = { from: string; to: string; spaceId: string; color: string };

export function GroupMap({ group, onOpenDevice }: {
  group: { id: string; spaces: Space[] };
  onOpenDevice: (spaceId: string, deviceId: string) => void;
}) {
  const host = useRef<HTMLDivElement>(null);
  const [size, setSize] = useState({ width: 0, height: 0 });
  const [view, setView] = useState(() => centerOf(55.751244, 37.618423, 4));
  const fitted = useRef("");

  const { markers, lines, tunnels, unplaced } = useMemo(() => collect(group.spaces), [group.spaces]);

  useEffect(() => {
    const node = host.current;
    if (!node) return;
    const update = () => setSize({ width: node.clientWidth, height: node.clientHeight });
    update();
    const observer = new ResizeObserver(update);
    observer.observe(node);
    return () => observer.disconnect();
  }, []);

  useEffect(() => {
    const node = host.current;
    if (!node) return;
    const onWheel = (event: WheelEvent) => {
      event.preventDefault();
      const rect = node.getBoundingClientRect();
      const ox = event.clientX - rect.left - rect.width / 2;
      const oy = event.clientY - rect.top - rect.height / 2;
      setView((current) => {
        const next = clamp(current.zoom + (event.deltaY < 0 ? 1 : -1), 3, 18);
        if (next === current.zoom) return current;
        const factor = 2 ** (next - current.zoom);
        return { zoom: next, x: (current.x + ox) * factor - ox, y: (current.y + oy) * factor - oy };
      });
    };
    node.addEventListener("wheel", onWheel, { passive: false });
    return () => node.removeEventListener("wheel", onWheel);
  }, []);

  useEffect(() => {
    if (size.width === 0 || markers.length === 0 || fitted.current === group.id) return;
    fitted.current = group.id;
    setView(fit(markers, size.width, size.height));
  }, [group.id, markers, size.width, size.height]);

  function drag(event: ReactPointerEvent<HTMLDivElement>) {
    if (event.button !== 0 || (event.target as HTMLElement).closest("button")) return;
    const startX = event.clientX;
    const startY = event.clientY;
    const origin = view;
    const target = event.currentTarget;
    target.setPointerCapture(event.pointerId);
    const move = (next: PointerEvent) => {
      setView({ zoom: origin.zoom, x: origin.x - (next.clientX - startX), y: origin.y - (next.clientY - startY) });
    };
    const up = () => {
      target.removeEventListener("pointermove", move);
      target.removeEventListener("pointerup", up);
    };
    target.addEventListener("pointermove", move);
    target.addEventListener("pointerup", up);
  }

  const tiles = size.width === 0 ? [] : tileList(view, size.width, size.height);
  const screen = (latitude: number, longitude: number) => {
    const point = project(latitude, longitude, view.zoom);
    return { x: point.x - view.x + size.width / 2, y: point.y - view.y + size.height / 2 };
  };
  const byKey = new Map(markers.map((item) => [item.spaceId + "\n" + item.publicKey.toLowerCase(), item]));

  return (
    <div className="group-map" ref={host} onPointerDown={drag}>
      {tiles.map((tile) => (
        <img key={tile.key} className="group-map-tile" alt="" src={tile.url} style={{ left: tile.left, top: tile.top }} />
      ))}
      <svg className="group-map-links" width={size.width} height={size.height}>
        {lines.map((line) => {
          const from = byKey.get(line.spaceId + "\n" + line.from.toLowerCase());
          const to = byKey.get(line.spaceId + "\n" + line.to.toLowerCase());
          if (!from || !to) return null;
          const a = screen(from.latitude, from.longitude);
          const b = screen(to.latitude, to.longitude);
          return <line key={line.spaceId + line.from + line.to} x1={a.x} y1={a.y} x2={b.x} y2={b.y} stroke={line.color} />;
        })}
      </svg>
      {markers.map((marker) => {
        const at = screen(marker.latitude, marker.longitude);
        const device = group.spaces.find((item) => item.id === marker.spaceId)?.devices.find((item) => item.live?.publicKey.toLowerCase() === marker.publicKey.toLowerCase());
        return (
          <button
            key={marker.spaceId + marker.publicKey}
            type="button"
            className={marker.repeater ? "map-node repeater" : "map-node companion"}
            style={{ left: at.x, top: at.y, borderColor: marker.color }}
            title={`${marker.name || "узел"} · ${marker.spaceName}`}
            onClick={() => { if (device) onOpenDevice(marker.spaceId, device.id); }}
          >
            <span>{marker.name || marker.publicKey.slice(0, 4)}</span>
          </button>
        );
      })}
      <div className="group-map-legend">
        <p>Тунель — пространство. Узел — участник, у которого в трафике есть координаты. Линия — шаг маршрута. Квадрат — репитер.</p>
        {tunnels.length === 0 && <p>Тунелей с просмотром нет.</p>}
        {markers.length === 0 && tunnels.length > 0 && <p>Участников с координатами ещё нет.</p>}
        {unplaced.length > 0 && <p>Без координат: {unplaced.join(", ")}</p>}
        {tunnels.map((item) => (
          <p key={item.id}><i style={{ background: item.color }} />{item.name}</p>
        ))}
      </div>
      <button type="button" className="secondary map-fit" onClick={() => markers.length > 0 && size.width > 0 && setView(fit(markers, size.width, size.height))}>Вся сеть</button>
      <p className="map-credit">© OpenStreetMap</p>
    </div>
  );
}

function collect(spaces: Space[]) {
  const visible = spaces.filter((space) => space.privileges.includes("view"));
  const tunnels = visible.map((space, index) => ({ id: space.id, name: space.name, color: COLORS[index % COLORS.length] }));
  const color = new Map(tunnels.map((item) => [item.id, item.color]));
  const markers: Marker[] = [];
  const lines: Line[] = [];
  const unplaced: string[] = [];
  for (const space of visible) {
    for (const node of space.map?.nodes ?? []) {
      markers.push({ ...node, spaceId: space.id, spaceName: space.name, color: color.get(space.id) ?? COLORS[0] });
    }
    for (const link of space.map?.links ?? []) {
      lines.push({ ...link, spaceId: space.id, color: color.get(space.id) ?? COLORS[0] });
    }
    for (const name of space.map?.unplaced ?? []) {
      if (!unplaced.includes(name)) unplaced.push(name);
    }
  }
  return { markers, lines, tunnels, unplaced };
}

function centerOf(latitude: number, longitude: number, zoom: number) {
  const point = project(latitude, longitude, zoom);
  return { zoom, x: point.x, y: point.y };
}

function fit(markers: Marker[], width: number, height: number) {
  let chosen = centerOf(markers[0].latitude, markers[0].longitude, 14);
  for (let zoom = 16; zoom >= 3; zoom -= 1) {
    const points = markers.map((item) => project(item.latitude, item.longitude, zoom));
    const minX = Math.min(...points.map((item) => item.x));
    const maxX = Math.max(...points.map((item) => item.x));
    const minY = Math.min(...points.map((item) => item.y));
    const maxY = Math.max(...points.map((item) => item.y));
    if (maxX - minX < width - 80 && maxY - minY < height - 80) {
      chosen = { zoom, x: (minX + maxX) / 2, y: (minY + maxY) / 2 };
      break;
    }
    chosen = { zoom, x: (minX + maxX) / 2, y: (minY + maxY) / 2 };
  }
  return chosen;
}

function tileList(view: { zoom: number; x: number; y: number }, width: number, height: number) {
  const zoom = view.zoom;
  const left = view.x - width / 2;
  const top = view.y - height / 2;
  const max = 2 ** zoom;
  const x0 = Math.floor(left / 256);
  const y0 = Math.floor(top / 256);
  const x1 = Math.floor((left + width) / 256);
  const y1 = Math.floor((top + height) / 256);
  const tiles: { key: string; url: string; left: number; top: number }[] = [];
  for (let x = x0; x <= x1; x += 1) {
    for (let y = y0; y <= y1; y += 1) {
      if (y < 0 || y >= max) continue;
      const wrapped = ((x % max) + max) % max;
      tiles.push({
        key: `${zoom}/${x}/${y}`,
        url: `https://tile.openstreetmap.org/${zoom}/${wrapped}/${y}.png`,
        left: x * 256 - left,
        top: y * 256 - top
      });
    }
  }
  return tiles;
}

function project(latitude: number, longitude: number, zoom: number) {
  const scale = 256 * 2 ** zoom;
  const lat = clamp(latitude, -85.051128, 85.051128) * Math.PI / 180;
  return {
    x: (longitude + 180) / 360 * scale,
    y: (1 - Math.log(Math.tan(lat) + 1 / Math.cos(lat)) / Math.PI) / 2 * scale
  };
}

function clamp(value: number, min: number, max: number) {
  return Math.min(max, Math.max(min, value));
}
