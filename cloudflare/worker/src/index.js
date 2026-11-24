import { DurableObject } from "cloudflare:workers";
import { SessionRoomLogic } from "./session-room.js";

export class SessionRoom extends DurableObject {
  constructor(ctx, env) {
    super(ctx, env);
    this.logic = new SessionRoomLogic(this.ctx, env);
  }
  fetch(request) { return this.logic.fetch(request); }
  webSocketMessage(socket, message) { return this.logic.webSocketMessage(socket, message); }
  webSocketClose(socket, code, reason, wasClean) {
    return this.logic.webSocketClose(socket, code, reason, wasClean);
  }
  webSocketError(socket, error) { return this.logic.webSocketError(socket, error); }
}

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    if (url.pathname === "/healthz" && request.method === "GET") {
      return Response.json({
        name: "ListenTogether edge relay", status: "ready",
        protocol: "SignalR JSON over WebSockets", media: "Opus packet relay",
        routerConfigurationRequired: false
      });
    }
    if (url.pathname !== "/hubs/listening")
      return new Response("Not found.", { status: 404 });
    if (request.headers.get("Upgrade")?.toLowerCase() !== "websocket")
      return new Response("WebSocket upgrade required.", { status: 426 });
    const id = env.SESSIONS.idFromName("listen-together-global");
    return env.SESSIONS.get(id).fetch(request);
  }
};
