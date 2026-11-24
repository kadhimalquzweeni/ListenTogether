const RECORD_SEPARATOR = "\u001e";
const CODE_ALPHABET = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
const MAX_PARTICIPANTS = 16;
const MAX_PACKET_BYTES = 1_275;
const MAX_BATCH_PACKETS = 5;
const MAX_MESSAGE_CHARS = 16 * 1024;

const frame = value => JSON.stringify(value) + RECORD_SEPARATOR;
const event = (socket, target, args) => {
  if (socket.readyState === 1) socket.send(frame({ type: 1, target, arguments: args }));
};
const completion = (socket, invocationId, result, error) => {
  if (!invocationId || socket.readyState !== 1) return;
  const value = { type: 3, invocationId };
  if (error) value.error = String(error);
  else if (result !== undefined) value.result = result;
  socket.send(frame(value));
};
const participant = attachment => ({
  displayName: attachment.displayName,
  isHost: attachment.role === "host"
});
function decodePacket(value) {
  if (typeof value !== "string" || value.length === 0 || value.length > 1_704 ||
      !/^[A-Za-z0-9+/]*={0,2}$/.test(value))
    throw new Error("An Opus packet is malformed or too large.");
  let binary;
  try { binary = atob(value); }
  catch { throw new Error("An Opus packet is not valid base64."); }
  if (binary.length < 1 || binary.length > MAX_PACKET_BYTES)
    throw new Error("Opus packets must contain between 1 and 1,275 bytes.");
  return value;
}
function createCode() {
  const bytes = new Uint8Array(8);
  crypto.getRandomValues(bytes);
  return Array.from(bytes, value => CODE_ALPHABET[value % CODE_ALPHABET.length]).join("");
}

export class SessionRoomLogic {
  constructor(ctx, env) { this.ctx = ctx; this.env = env; }

  async fetch(request) {
    if (request.headers.get("Upgrade")?.toLowerCase() !== "websocket")
      return new Response("WebSocket upgrade required.", { status: 426 });
    const pair = new WebSocketPair();
    const [client, server] = Object.values(pair);
    server.serializeAttachment({
      id: crypto.randomUUID(), handshaken: false, code: null, role: null,
      displayName: null, lastSequence: -1, lastBatchAt: 0
    });
    this.ctx.acceptWebSocket(server);
    return new Response(null, { status: 101, webSocket: client });
  }

  async webSocketMessage(socket, data) {
    if (typeof data !== "string" || data.length > MAX_MESSAGE_CHARS) {
      socket.close(1009, "Only bounded SignalR JSON messages are supported.");
      return;
    }
    let attachment = socket.deserializeAttachment();
    const records = data.split(RECORD_SEPARATOR).filter(Boolean);
    if (!attachment?.handshaken) {
      if (!records.length) return;
      let handshake;
      try { handshake = JSON.parse(records.shift()); }
      catch { socket.close(1002, "Invalid SignalR handshake."); return; }
      if (handshake?.protocol !== "json" || handshake?.version !== 1) {
        socket.send(frame({ error: "Only the SignalR JSON protocol version 1 is supported." }));
        socket.close(1002, "Unsupported SignalR protocol.");
        return;
      }
      attachment = { ...attachment, handshaken: true };
      socket.serializeAttachment(attachment);
      socket.send(frame({}));
    }

    for (const record of records) {
      let message;
      try { message = JSON.parse(record); }
      catch { socket.close(1002, "Malformed SignalR message."); return; }
      if (message.type === 6) {
        socket.send(frame({ type: 6 }));
        continue;
      }
      if (message.type === 7) {
        socket.close(1000, "Client closed the SignalR connection.");
        return;
      }
      if (message.type !== 1 || typeof message.target !== "string") continue;
      try {
        const result = this.invoke(socket, message.target, message.arguments ?? []);
        completion(socket, message.invocationId, result);
      } catch (error) {
        if (message.invocationId) completion(socket, message.invocationId, undefined, error.message);
        else {
          socket.send(frame({ type: 7, error: String(error.message), allowReconnect: true }));
          socket.close(1008, "Hub invocation rejected.");
          return;
        }
      }
    }
  }

  invoke(socket, target, args) {
    const attachment = socket.deserializeAttachment() ?? {};
    switch (target) {
      case "CreateSession": return this.createSession(socket, attachment, args[0]);
      case "JoinSession": return this.joinSession(socket, attachment, args[0], args[1]);
      case "SendAudioPacket": return this.sendAudioBatch(socket, attachment, [args[0]], [args[1]]);
      case "SendAudioPacketBatch": return this.sendAudioBatch(socket, attachment, args[0], args[1]);
      case "LeaveSession": this.leaveSession(socket, attachment); return null;
      default: throw new Error("Unknown hub method.");
    }
  }

  createSession(socket, attachment, displayName) {
    if (attachment.code) throw new Error("This connection is already in a session.");
    const name = this.validateName(displayName);
    let code;
    do { code = createCode(); } while (this.roomSockets(code).length);
    socket.serializeAttachment({
      ...attachment, code, role: "host", displayName: name,
      lastSequence: -1, lastBatchAt: 0
    });
    return { code, joinUri: null };
  }

  joinSession(socket, attachment, code, displayName) {
    if (attachment.code) throw new Error("This connection is already in a session.");
    const normalized = this.normalizeCode(code);
    const members = this.roomSockets(normalized);
    const host = members.find(member => member.deserializeAttachment()?.role === "host");
    if (!host) throw new Error("That listening session doesn't exist or has already ended.");
    if (members.length >= MAX_PARTICIPANTS)
      throw new Error("This session has reached its participant limit.");
    const joinedAttachment = {
      ...attachment, code: normalized, role: "listener",
      displayName: this.validateName(displayName), lastSequence: -1, lastBatchAt: 0
    };
    socket.serializeAttachment(joinedAttachment);
    const joined = participant(joinedAttachment);
    for (const member of members) event(member, "ParticipantJoined", [joined]);
    return {
      invitation: { code: normalized, joinUri: null },
      hostDisplayName: host.deserializeAttachment().displayName,
      participants: [...members, socket].map(member => participant(member.deserializeAttachment())),
      joinedParticipant: joined
    };
  }

  sendAudioBatch(socket, attachment, encodedPackets, sequences) {
    if (attachment.role !== "host" || !attachment.code)
      throw new Error("Only the connected session host can publish audio.");
    if (!Array.isArray(encodedPackets) || !Array.isArray(sequences) ||
        encodedPackets.length < 1 || encodedPackets.length > MAX_BATCH_PACKETS ||
        encodedPackets.length !== sequences.length)
      throw new Error("Audio batches must contain between one and five matching packets and sequences.");
    const packets = encodedPackets.map(decodePacket);
    for (let index = 0; index < sequences.length; index++) {
      if (!Number.isSafeInteger(sequences[index]) || sequences[index] < 0 ||
          (index > 0 && sequences[index] <= sequences[index - 1]))
        throw new Error("Packet sequences must be non-negative and strictly increasing.");
    }
    if (sequences[0] <= attachment.lastSequence)
      throw new Error("Audio packet sequence was replayed or is out of order.");
    const now = Date.now();
    if (attachment.lastBatchAt && now - attachment.lastBatchAt < 20)
      throw new Error("Audio packets are arriving too quickly.");
    socket.serializeAttachment({
      ...attachment, lastSequence: sequences[sequences.length - 1], lastBatchAt: now
    });
    const listeners = this.roomSockets(attachment.code)
      .filter(member => member !== socket && member.deserializeAttachment()?.role === "listener");
    for (let index = 0; index < packets.length; index++)
      for (const listener of listeners)
        event(listener, "ReceiveAudioPacket", [packets[index], sequences[index]]);
    return null;
  }

  leaveSession(socket, attachment) {
    if (!attachment.code) return;
    const members = this.roomSockets(attachment.code);
    if (attachment.role === "host") {
      for (const member of members) {
        event(member, "SessionEnded", ["The host ended the listening session."]);
        const current = member.deserializeAttachment();
        member.serializeAttachment({
          ...current, code: null, role: null, lastSequence: -1, lastBatchAt: 0
        });
      }
    } else {
      for (const member of members)
        if (member !== socket) event(member, "ParticipantLeft", [participant(attachment)]);
      socket.serializeAttachment({
        ...attachment, code: null, role: null, lastSequence: -1, lastBatchAt: 0
      });
    }
  }

  webSocketClose(socket) {
    const attachment = socket.deserializeAttachment();
    if (!attachment?.code) return;
    const members = this.roomSockets(attachment.code);
    if (attachment.role === "host") {
      for (const member of members) {
        if (member === socket) continue;
        event(member, "SessionEnded", ["The host disconnected."]);
        member.close(4001, "The host disconnected.");
      }
    } else {
      for (const member of members)
        if (member !== socket) event(member, "ParticipantLeft", [participant(attachment)]);
    }
  }
  webSocketError(socket) { return this.webSocketClose(socket); }
  roomSockets(code) {
    return this.ctx.getWebSockets().filter(socket =>
      socket.readyState === 1 && socket.deserializeAttachment()?.code === code);
  }
  validateName(value) {
    if (typeof value !== "string" || !value.trim() || value.trim().length > 32)
      throw new Error("Display names must contain between 1 and 32 characters.");
    return value.trim();
  }
  normalizeCode(value) {
    if (typeof value !== "string") throw new Error("A session code is required.");
    const code = value.trim().toUpperCase();
    if (code.length !== 8 || [...code].some(character => !CODE_ALPHABET.includes(character)))
      throw new Error("The session code is invalid.");
    return code;
  }
}

