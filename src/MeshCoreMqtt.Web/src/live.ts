import { token } from "./api";

export type LiveWatch = "tree" | "activity" | "nodes";

type LiveHandlers = {
  onTree?: (tree: unknown) => void;
  onActivity?: (rows: unknown) => void;
  onNodes?: (rows: unknown) => void;
};

function liveUrl(watch: LiveWatch[]) {
  const protocol = location.protocol === "https:" ? "wss:" : "ws:";
  const params = new URLSearchParams({
    access_token: token() ?? "",
    watch: watch.join(",")
  });
  return `${protocol}//${location.host}/api/ws/live?${params}`;
}

export function subscribeLive(watch: LiveWatch[], handlers: LiveHandlers) {
  const current = token();
  if (!current) return () => undefined;

  let ws: WebSocket | null = null;
  let closed = false;
  let retryMs = 1000;

  function connect() {
    if (closed) return;
    ws = new WebSocket(liveUrl(watch));
    ws.onmessage = (event) => {
      retryMs = 1000;
      let body: Record<string, unknown>;
      try {
        body = JSON.parse(event.data as string);
      } catch {
        return;
      }
      if (body.tree !== undefined) handlers.onTree?.(body.tree);
      if (body.activity !== undefined) handlers.onActivity?.(body.activity);
      if (body.nodes !== undefined) handlers.onNodes?.(body.nodes);
    };
    ws.onclose = () => {
      ws = null;
      if (closed) return;
      window.setTimeout(connect, retryMs);
      retryMs = Math.min(retryMs * 2, 15000);
    };
    ws.onerror = () => ws?.close();
  }

  connect();
  return () => {
    closed = true;
    ws?.close();
  };
}
