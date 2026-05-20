/**
 * Admin WS client. Pinia/Vue components subscribe to specific topics
 * and a callback. Reconnects automatically with exponential backoff.
 */

export type AdminEvent =
  | { type: 'job'; jobId: string; ts: string; [k: string]: unknown }
  | { type: 'workstation'; ts: string; [k: string]: unknown }
  | { type: 'hello'; subscribed: string[] }
  | { type: 'pong'; ts: string };

type Handler = (ev: AdminEvent) => void;

class AdminWs {
  private socket?: WebSocket;
  private subs = new Set<string>(['jobs', 'workstations']);
  private handlers = new Set<Handler>();
  private retry = 0;
  private closed = false;

  connect(path: string = '/ws/admin'): void {
    this.closed = false;
    const url = (location.protocol === 'https:' ? 'wss://' : 'ws://') + location.host + path;
    const socket = new WebSocket(url);
    this.socket = socket;

    socket.addEventListener('open', () => {
      this.retry = 0;
      socket.send(JSON.stringify({ type: 'subscribe', topics: [...this.subs] }));
    });

    socket.addEventListener('message', (e) => {
      let parsed: AdminEvent;
      try { parsed = JSON.parse(e.data); } catch { return; }
      for (const h of this.handlers) h(parsed);
    });

    socket.addEventListener('close', () => {
      if (this.closed) return;
      const wait = Math.min(30_000, 500 * Math.pow(2, this.retry++));
      setTimeout(() => this.connect(path), wait);
    });

    socket.addEventListener('error', () => {
      try { socket.close(); } catch { /* ignore */ }
    });
  }

  subscribe(topic: string): void {
    this.subs.add(topic);
    if (this.socket?.readyState === WebSocket.OPEN) {
      this.socket.send(JSON.stringify({ type: 'subscribe', topics: [topic] }));
    }
  }

  unsubscribe(topic: string): void {
    this.subs.delete(topic);
    if (this.socket?.readyState === WebSocket.OPEN) {
      this.socket.send(JSON.stringify({ type: 'unsubscribe', topics: [topic] }));
    }
  }

  on(handler: Handler): () => void {
    this.handlers.add(handler);
    return () => this.handlers.delete(handler);
  }

  disconnect(): void {
    this.closed = true;
    try { this.socket?.close(); } catch { /* ignore */ }
  }
}

export const adminWs = new AdminWs();
