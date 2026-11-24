import test from "node:test";
import assert from "node:assert/strict";
import { SessionRoomLogic as SessionRoom } from "../src/session-room.js";

const RS = "\u001e";
class FakeSocket {
  constructor() {
    this.readyState = 1;
    this.frames = [];
    this.attachment = {
      id: crypto.randomUUID(), handshaken: false, code: null, role: null,
      displayName: null, lastSequence: -1, lastBatchAt: 0
    };
    this.closed = null;
  }
  serializeAttachment(value) { this.attachment = structuredClone(value); }
  deserializeAttachment() { return structuredClone(this.attachment); }
  send(frame) { if (this.readyState === 1) this.frames.push(frame); }
  close(code = 1000, reason = "") { this.closed = { code, reason }; this.readyState = 3; }
  takeMessages() {
    const frames = this.frames.splice(0);
    return frames.flatMap(frame => frame.split(RS).filter(Boolean).map(value => JSON.parse(value)));
  }
}
class FakeContext {
  constructor() { this.sockets = []; }
  acceptWebSocket(socket) { this.sockets.push(socket); }
  getWebSockets() { return this.sockets.filter(socket => socket.readyState === 1); }
}
async function connect(room, context) {
  const socket = new FakeSocket();
  context.acceptWebSocket(socket);
  await room.webSocketMessage(socket, JSON.stringify({ protocol: "json", version: 1 }) + RS);
  assert.deepEqual(socket.takeMessages(), [{}]);
  return socket;
}
async function invoke(room, socket, target, args, invocationId = "1") {
  await room.webSocketMessage(socket, JSON.stringify({
    type: 1, invocationId, target, arguments: args
  }) + RS);
  return socket.takeMessages();
}
function completion(messages, id = "1") {
  return messages.find(message => message.type === 3 && message.invocationId === id);
}

test("SignalR handshake rejects unsupported protocols", async () => {
  const ctx = new FakeContext(), room = new SessionRoom(ctx, {});
  const socket = new FakeSocket();
  ctx.acceptWebSocket(socket);
  await room.webSocketMessage(socket, JSON.stringify({ protocol: "messagepack", version: 1 }) + RS);
  assert.equal(socket.closed.code, 1002);
});

test("host creates invite and listener joins with participant notifications", async () => {
  const ctx = new FakeContext(), room = new SessionRoom(ctx, {});
  const host = await connect(room, ctx);
  const created = completion(await invoke(room, host, "CreateSession", ["Host"]));
  assert.match(created.result.code, /^[A-HJ-NP-Z2-9]{8}$/);
  const listener = await connect(room, ctx);
  const joined = completion(await invoke(room, listener, "JoinSession", [created.result.code, "Listener"]));
  assert.equal(joined.result.hostDisplayName, "Host");
  assert.equal(joined.result.participants.length, 2);
  assert.deepEqual(joined.result.joinedParticipant, { displayName: "Listener", isHost: false });
  const event = host.takeMessages()[0];
  assert.equal(event.target, "ParticipantJoined");
  assert.equal(event.arguments[0].displayName, "Listener");
});

test("batched Opus packets are forwarded as individual audio events", async () => {
  const ctx = new FakeContext(), room = new SessionRoom(ctx, {});
  const host = await connect(room, ctx);
  const created = completion(await invoke(room, host, "CreateSession", ["Host"]));
  const listener = await connect(room, ctx);
  await invoke(room, listener, "JoinSession", [created.result.code, "Listener"]);
  host.takeMessages();
  const packets = [Buffer.from([1, 2, 3]).toString("base64"), Buffer.from([4, 5]).toString("base64")];
  await room.webSocketMessage(host, JSON.stringify({
    type: 1, target: "SendAudioPacketBatch", arguments: [packets, [10, 11]]
  }) + RS);
  const received = listener.takeMessages().filter(message => message.target === "ReceiveAudioPacket");
  assert.equal(received.length, 2);
  assert.deepEqual(received[0].arguments, [packets[0], 10]);
  assert.deepEqual(received[1].arguments, [packets[1], 11]);
});

test("listener cannot publish audio and malformed batches are rejected", async () => {
  const ctx = new FakeContext(), room = new SessionRoom(ctx, {});
  const host = await connect(room, ctx);
  const created = completion(await invoke(room, host, "CreateSession", ["Host"]));
  const listener = await connect(room, ctx);
  await invoke(room, listener, "JoinSession", [created.result.code, "Listener"]);
  const denied = completion(await invoke(room, listener, "SendAudioPacketBatch", [
    [Buffer.from([1]).toString("base64")], [1]
  ], "denied"));
  assert.match(denied.error, /Only the connected session host/);
  const malformed = completion(await invoke(room, host, "SendAudioPacketBatch", [
    [Buffer.from([1]).toString("base64"), Buffer.from([2]).toString("base64")], [1]
  ], "bad"));
  assert.match(malformed.error, /matching packets and sequences/);
});

test("host disconnect ends room and notifies listeners", async () => {
  const ctx = new FakeContext(), room = new SessionRoom(ctx, {});
  const host = await connect(room, ctx);
  const created = completion(await invoke(room, host, "CreateSession", ["Host"]));
  const listener = await connect(room, ctx);
  await invoke(room, listener, "JoinSession", [created.result.code, "Listener"]);
  listener.takeMessages();
  await room.webSocketClose(host, 1006, "network lost", false);
  const event = listener.takeMessages()[0];
  assert.equal(event.target, "SessionEnded");
  assert.match(event.arguments[0], /host disconnected/i);
  assert.equal(listener.closed.code, 4001);
});

test("room enforces sixteen participants", async () => {
  const ctx = new FakeContext(), room = new SessionRoom(ctx, {});
  const host = await connect(room, ctx);
  const created = completion(await invoke(room, host, "CreateSession", ["Host"]));
  for (let index = 1; index < 16; index++) {
    const listener = await connect(room, ctx);
    const result = completion(await invoke(room, listener, "JoinSession", [
      created.result.code, "Listener " + index
    ]));
    assert.ok(result.result);
  }
  const overflow = await connect(room, ctx);
  const result = completion(await invoke(room, overflow, "JoinSession", [
    created.result.code, "One too many"
  ]));
  assert.match(result.error, /participant limit/);
});
