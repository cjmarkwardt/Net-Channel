# Wire Format

Net-Channel runs over UDP: every message is exchanged as a single, unordered, unreliable datagram, with no
guarantee of delivery, ordering, or an intact underlying network-layer checksum. This document describes
the byte layout of a datagram, the corruption check applied to it, the four-step handshake used to establish
a connection before any application traffic flows, the per-packet authentication that protects the
connection afterward, the shared reply several kinds of packet get back, the heartbeat that detects a
silently lost connection, how a property set and a method call are each delivered, and the messages used to
synchronize net entities over an established connection.

Message bodies are serialized with [Protocol Buffers](https://protobuf.dev) (proto3). The `.proto` schemas
referenced below live in [`Core/Proto/`](../Core/Proto/).

## Datagram layout

Every UDP datagram sent or received by Net-Channel starts with a 1-byte flag, followed optionally by a
checksum, followed by the packet itself:

| Bytes | Field | Description |
|---|---|---|
| 0 | `has_checksum` | A boolean: `0` means false, any other value means true. True means a `checksum` prefix follows (`## Checksum` below); false means `packet` starts immediately after this byte. |
| 1-4 (only if `has_checksum` is true) | `checksum` | CRC32C of `packet`, little-endian `uint32`. |
| next byte onward | `packet` | A serialized `netchannel.wire.Packet` message (see [`Packet.proto`](../Core/Proto/Packet.proto)). |

A receiver reads `has_checksum` first, before attempting to interpret anything else, so it always knows
whether the next 4 bytes are a `checksum` or the start of `packet` itself — there's no other way to tell, since
`packet`'s own fields (including `connection_id`, which would otherwise be the natural thing to branch on)
only become readable once its start is known. Net-Channel itself only ever sends `0` or `1`; treating any
other value as true, rather than rejecting it as malformed, is what a receiver does with any other single-byte
boolean it doesn't control the origin of — the packet that follows is still fully subject to `## Checksum`
(if claimed) and `### Replay rejection` below either way, so a corrupted flag byte just leads to whichever of
the two parses eventually fails, not to anything being wrongly accepted.

`has_checksum` is true only for four of the five handshake packets — `## Checksum` below explains which and
why. Those four cost 1 byte more than an unconditional checksum with no flag would; every other, far more
frequent packet saves 3.

`Packet` carries `connection_id`, a `sequence`/`tag` pair, and a `oneof` selecting exactly one type-specific
message:

```protobuf
message Packet {
  uint64 connection_id = 1;
  uint64 sequence = 2;
  bytes tag = 3;

  oneof payload {
    HandshakeInit handshake_init = 4;
    HandshakeChallenge handshake_challenge = 5;
    HandshakeResponse handshake_response = 6;
    HandshakeAccept handshake_accept = 7;
    HandshakeReject handshake_reject = 8;
    Disconnect disconnect = 9;
    Ping ping = 10;
    EntityCreate entity_create = 11;
    EntityDestroy entity_destroy = 12;
    EntitySet entity_set = 13;
    EntityCall entity_call = 14;
    Reply reply = 15;
  }
}
```

- Whichever `oneof` field is set determines how the packet is handled, in place of a separate type tag over
  an opaque body; each handshake step, `Disconnect`, `Ping` (`## Liveness` below), each entity message
  (`## Entities` below), and the shared `Reply` (`## Responses` below) has its own message schema. All
  network interaction flows through entities — there is no generic opaque message type for arbitrary
  application data.
- `connection_id` is a server-assigned, sequential 64-bit id used to route a datagram to the right connection.
  It exists independently of source IP/port so that a connection survives the client's address changing
  (e.g. a NAT rebind or network switch). It is `0` for the handshake packets exchanged before a connection
  id has been assigned (`handshake_init`/`handshake_challenge`/`handshake_response`).
- `sequence` and `tag` authenticate every packet that carries a real `connection_id` (`handshake_accept`,
  `disconnect`, `ping`, every entity message, and `reply`), so that knowing or guessing a `connection_id`
  alone is not enough to inject traffic into someone else's connection. Both are zero/empty on the handshake
  packets sent before a `connection_id` exists, which rely on the handshake's own cookie instead. See
  `## Packet authentication` below.
- `connection_id` and `sequence` are `uint64` (`varint`-encoded), not `fixed64`, because both are small,
  slowly/steadily-growing counters rather than random values: a `varint` costs 1 byte up to 127 and only
  grows past `fixed64`'s constant 8 bytes once a value exceeds 2^56, which neither field reaches within any
  realistic connection's lifetime. This is the opposite tradeoff from `checksum`/`tag`, which are
  high-entropy hash/MAC output and stay fixed-width (`fixed32`) or a raw byte string (`bytes`) for that
  reason.
- There is no protocol version on `Packet`. Version compatibility is checked only during the initial
  handshake (`HandshakeInit.protocol_version`, below); once a connection is accepted, both sides already
  know it matched, so it is never repeated on later packets.

## Checksum

`checksum` exists only where `tag` isn't available yet to cover the same ground: `## Packet authentication`
below gives every packet carrying a real `connection_id` a keyed MAC that corrupted or forged bytes fail
regardless of whether they'd also fail a CRC first, so a checksum on top of `tag` would only ever reject what
`tag` was already going to reject, just more expensively for anything that gets far enough to be checked at
all. `HandshakeInit`/`HandshakeChallenge`/`HandshakeResponse` are exchanged before a `connection_id` exists at
all, and `HandshakeReject` never gets one either, since the server allocates no state along that path — these
four have no `tag` to fall back on, so they're the only packets with `has_checksum` set. Every other packet,
including `HandshakeAccept` (which, unlike the other four, already carries a real `connection_id` and `tag`
of its own — `### 4. HandshakeAccept / HandshakeReject` below), omits `checksum` entirely.

A packet with `has_checksum` set (`## Datagram layout` above) is prefixed with a CRC32C (Castagnoli, the
polynomial used by iSCSI/QUIC and accelerated by the `crc32` instruction on modern x86/ARM CPUs) checksum of
the serialized `Packet` that follows it. UDP's own checksum is weak, and disabled entirely by some IPv4
stacks, so it is not sufficient on its own to protect against corruption reaching the application.

A receiver computes CRC32C over the bytes after the checksum field and discards the datagram outright if it
does not match, before making any attempt to parse `Packet`. This keeps a corrupted or truncated datagram —
and, incidentally, most non-protocol traffic landing on the socket — from reaching the protobuf parser or
any handshake state, for exactly the four messages that would otherwise have nothing stronger checking them
first.

## Connection handshake

A connection begins with a four-step handshake between the connecting client and the listening server. The
server remains fully stateless for the first two steps: no per-attempt memory is allocated until the client
has proven it can receive traffic at its claimed source address. This defends against the two standard
threats to a UDP listener — a spoofed-source flood trying to exhaust server memory, and the listener itself
being abused as a reflection/amplification vector — without requiring any cryptographic handshake state to
be kept before the client's address is verified.

```
Client                                   Server
  |-------- 1. HandshakeInit ------------->|   (server allocates nothing)
  |<------- 2. HandshakeChallenge ---------|   (stateless cookie)
  |-------- 3. HandshakeResponse --------->|   (echoes the cookie back)
  |<------- 4. HandshakeAccept ------------|   (connection now exists)
```

Schemas for all five messages below are in [`Handshake.proto`](../Core/Proto/Handshake.proto). Alongside the
return-routability cookie, steps 1-2 also carry an ephemeral X25519 key exchange used to derive the
connection's session key, and, optionally, a server signature a client can check against an identity key it
already has pinned; see `### Server authentication` and `## Packet authentication` below.

### 1. `HandshakeInit` (client → server)

The client generates an ephemeral X25519 key pair and sends the single `protocol_version` it is running
(e.g. `"1.2.0"`), plus `client_public_key`. Neither side supports any version other than the single one it
runs, so the server does not allocate any state for this message; it either replies with a
`HandshakeChallenge`/`HandshakeReject`, or (if `protocol_version` doesn't exactly match the server's own)
drops it.

### 2. `HandshakeChallenge` (server → client)

Having confirmed `protocol_version` exactly matches its own, the server generates its own ephemeral X25519 key
pair (`server_public_key`) and derives a `cookie` — a value that lets it verify the next message without
having stored anything about this attempt:

```
cookie = HMAC-SHA256(server_secret, source_ip || source_port || client_public_key || server_public_key || issued_at)[0:16] || issued_at
```

`server_secret` is a value generated once at server startup and held only in memory; `issued_at` is a
4-byte Unix timestamp, appended in the clear so the server can recompute the HMAC later without storing
anything. If the server is configured with a long-term Ed25519 identity key, it also attaches
`server_signature`: a signature over `server_public_key` made with that private key. A server with no
identity key configured leaves `server_signature` empty, and only unauthenticated clients can proceed
(`### Server authentication` below). The `HandshakeChallenge` is kept no larger than the `HandshakeInit`
that triggered it, so that a spoofed-source flood cannot use the server as a bandwidth amplifier.

### 3. `HandshakeResponse` (client → server)

Before responding, the client validates `server_signature` per its own connection policy (`### Server
authentication` below); if validation fails, it aborts the attempt locally instead of sending a
`HandshakeResponse`. Otherwise it echoes `client_public_key`, `server_public_key`, and `cookie` back
unmodified. Only a client that actually received the `HandshakeChallenge` at its claimed source address can
produce this message, which is what proves return routability — a spoofed source address cannot complete
this step, since the challenge was sent to (and this response can only be produced by) whoever really
controls that address.

### Server authentication

A client connects in one of two modes, chosen locally and never signaled to the server:

- **Pinned** — the client already has an `expected_identity_key` (distributed out of band); the server
  never needs to send this key back, since the client isn't learning it from the wire, only using it to
  check what the wire sent. It requires `server_signature` to be present and to verify, under
  `expected_identity_key`, over `server_public_key`. Any failure — a missing signature or one that doesn't
  verify — aborts the handshake before `HandshakeResponse` is sent: an active attacker who substituted their
  own `server_public_key` cannot produce a signature that verifies under the real server's identity key
  without also having its private key.
- **Unauthenticated** — the client has no expected key and skips this validation entirely, accepting
  whatever `server_public_key` (and `server_signature`, if any) the `HandshakeChallenge` carries.

This is a raw public-key pin, like an SSH host key, not a certificate-authority chain — enough to detect an
active man-in-the-middle once the client already knows which server it means to reach, without requiring any
further PKI. A server with no identity key configured at all can only accept unauthenticated clients.

### 4. `HandshakeAccept` / `HandshakeReject` (server → client)

On receiving a `HandshakeResponse`, the server recomputes the expected `cookie` from the echoed fields plus
the embedded `issued_at`, using its `server_secret`, and compares it (constant-time) to the one the client
sent. It also checks `issued_at` against a short freshness window (30 seconds) to bound how long a captured
cookie remains replayable. Only once the cookie passes both checks does the server allocate real connection
state and assign the next sequential `connection_id`, returned in `HandshakeAccept`. `client_public_key` and
`server_public_key` are retained by both sides to derive the session key used to authenticate every later
packet (see `## Packet authentication` below), and, for a `NetSecureAttribute` method's traffic, to derive
payload encryption keys as well.

If the cookie is missing, invalid, or expired, or the client's `protocol_version` no longer exactly matches
the server's own (e.g. the server was reconfigured with a different version between steps 2 and 3), the
server replies with `HandshakeReject` instead, and allocates no state. `reason` is a free-form,
human-readable message describing which of those it was — not a fixed or enumerated set of values — meant
for logging/diagnostics rather than programmatic branching.

### Retries

A client that receives no `HandshakeChallenge`/`HandshakeReject` for a `HandshakeInit`, or no
`HandshakeAccept`/`HandshakeReject` for a `HandshakeResponse`, retransmits the corresponding message with
backoff, since UDP delivers no guarantee that either arrived. A `HandshakeResponse` retransmission carries
the same cookie, and is verified by the server exactly as the original was.

## Packet authentication

The handshake's cookie only proves return routability during connection setup; it is never re-checked
afterward. Without something more, a `connection_id` alone would be enough to inject `Disconnect` or entity
packets into someone else's connection, since routing by `connection_id` is deliberately independent of
source IP/port (see `## Datagram layout` above). `sequence` and `tag` close that gap: every packet carrying
a real `connection_id` — `HandshakeAccept`, `Disconnect`, every entity message (`## Entities` below), and
`Reply` (`## Responses` below) — must carry a valid one of each.

### Session key derivation

Each side combines its own ephemeral X25519 private key with the other side's public key to compute a
shared secret, then derives the connection's `session_key`:

```
shared_secret = X25519(client_private_key, server_public_key)   // computed by the client
              = X25519(server_private_key, client_public_key)   // computed by the server
session_key   = HKDF-SHA256(salt: client_public_key || server_public_key, ikm: shared_secret, info: "netchannel packet-auth v1")
```

The server holds `client_public_key` from `HandshakeInit` and generates its own key pair while building
`HandshakeChallenge`, so it can compute `shared_secret`/`session_key` immediately; the client holds both
public keys as soon as it receives that `HandshakeChallenge`. So `session_key` is available to both sides in
time to authenticate `HandshakeAccept` — no extra round trip beyond the four handshake steps.

Because computing `shared_secret` requires one of the two private keys, which never travel on the wire, a
passive eavesdropper who observes the entire handshake still cannot compute `session_key`. It does not, by
itself, defend against an *active* on-path attacker who intercepts `HandshakeChallenge` and substitutes
their own `server_public_key` — plain Diffie-Hellman is inherently open to that. A client only gets
protection from that attacker by connecting in **Pinned** mode (`### Server authentication` above), since
forging a signature that verifies under the real server's identity key requires the real server's private
identity key, which the substituted key alone doesn't provide.

### Tag computation

`tag` is the first 16 bytes of `HMAC-SHA256(session_key, packet)`, where `packet` is the serialized `Packet`
with the `tag` field itself cleared — unlike the CRC32C in `## Checksum`, which sits outside `Packet`
entirely, `tag` is a field of the message it covers, so it must be excluded from its own input rather than
prepended to it. Covering the whole packet, including `connection_id` and `sequence`, in the MAC input means
neither can be tampered with independently of the tag. Each sender keeps its own monotonically increasing
`sequence`, incremented once per packet sent on the connection; the two directions of a connection count
independently.

### Replay rejection

For each connection, a receiver tracks, per direction, the highest `sequence` accepted so far plus a sliding
window of recently-accepted values, to tolerate UDP reordering without treating every out-of-order packet as
a replay. A packet is discarded — before being handed to any handler — if its `tag` fails to verify, or if
its `sequence` falls outside that window or duplicates one already seen.

This authentication is applied uniformly to every packet, including a property set's: HMAC-SHA256 is cheap
enough (hardware-accelerated by the SHA extensions on modern x86/ARM CPUs) that authenticating one costs a
small fraction of what generating and transmitting it already does. It is independent of, and distinct from,
the payload confidentiality `### Payload encryption` below separately adds for a `NetSecureAttribute`
method's traffic.

### Payload encryption

A `NetSecureAttribute` method's traffic reuses the same ECDH shared secret as `session_key`, deriving an
independent key from it for confidentiality — the forward reference in `### 4. HandshakeAccept /
HandshakeReject` above:

```
payload_key = HKDF-SHA256(salt: client_public_key || server_public_key, ikm: shared_secret, info: "netchannel payload-encryption v1")
```

For such a method, each `EntityCall` argument and its `Reply.result` are ciphertext instead of plaintext.
Encrypting several independent values under one static `payload_key` needs a nonce that never repeats; rather
than adding a dedicated nonce field to the schema, a fresh key is derived per value from data the call itself
already carries:

```
value_key  = HKDF-SHA256(salt: none, ikm: payload_key, info: "netchannel payload value v1" || entity_id || method_id || operation || context)
ciphertext = ChaCha20-Poly1305-Seal(value_key, nonce: 12 zero bytes, plaintext: value, aad: connection_id || entity_id)
```

`entity_id`/`method_id`/`operation` are the call's own — `entity_id` is never reused for a different entity
(`## Entities` below), `operation` (`## Method delivery` below) is unique per distinct call on that method's
channel and, unlike `Packet.sequence`, deliberately stays the same across every retransmission of that same
call; `context` is a one-byte discriminator followed by the argument's position, for an `EntityCall` argument
(`0x00` || position), or the discriminator alone, for a `Reply.result` (`0x01`) — the discriminator byte means
the two can never collide regardless of how many arguments a method takes. Together,
`entity_id`/`method_id`/`operation`/`context` never repeat for any two different values either side ever
encrypts on the connection, so a fixed all-zero nonce under each one-off `value_key` never repeats either.
Both sides already know, from the shared `TController` interface, which `method_id` values carry
`[NetSecure]`, so the receiver knows exactly which fields on an incoming packet to decrypt.

Deriving `value_key` from `operation` instead of `Packet.sequence` is what lets a retransmission carry the
exact same ciphertext as the attempt before it: encryption happens once, when a call (or its result) is first
computed, and every resend of it (`## Method delivery` below) simply carries that same ciphertext again,
rather than re-encrypting under a fresh key on each attempt.

This is layered on top of, not a replacement for, `tag`: `tag` still covers the whole packet — ciphertext
included — regardless of encryption, so a `NetSecureAttribute` method's packet is both authenticated and
confidential, while every other packet is authenticated only.

## Responses

A `Ping`, `EntityCreate`, `EntityDestroy`, `EntitySet`, and `EntityCall` each get exactly one specific answer
back, correlated to whichever packet prompted it. Rather than a separate message per case, one shared `Reply`
(schema in [`Packet.proto`](../Core/Proto/Packet.proto)) covers all of them:

```protobuf
message Reply {
  uint64 in_response_to = 1;

  oneof outcome {
    bytes result = 2;
    string failure = 3;
  }
}
```

- `in_response_to` is the `Packet.sequence` of the packet being answered, correlating a `Reply` back to
  whichever pending wait it satisfies.
- Neither `result` nor `failure` set is a plain acknowledgment: a `Ping`'s reply, or an
  `EntityCreate`/`EntityDestroy`/`EntitySet`/`void` `EntityCall` accepted with nothing further to report.
- `result` is set for a `Task`/`Task<T>`-returning `EntityCall` that completed successfully: the method's
  return value, encoded the same way as an `EntityCall` argument, including encryption for a
  `NetSecureAttribute` method (`### Payload encryption` above). Empty for a method returning a bare `Task`.
- `failure` is set when an entity's owner does not apply an `EntitySet` or complete an `EntityCall` — either
  because it declines to honor it (e.g. a member with no `NetAccessAttribute` access, a `NetRoleAttribute`
  the sender's connection doesn't have, or an unrecognized property/method) or because its controller method
  threw while executing an otherwise-permitted call, in which case `failure` is a generic message rather than
  the thrown exception's own, to avoid leaking the owner's internal error details. Otherwise a free-form,
  human-readable message, not a fixed or enumerated set of values, the same as `HandshakeReject.reason`. On
  the C# side (`Docs/Entities.md#remote-entities`), a failed `Task`/`Task<T>`-returning `EntityCall` faults
  that call's task with a `NetFailedException` carrying `failure`; either way — faulted task or not — the
  failure also triggers `INetManager.Failed`.

A `Reply` never has any ambiguity about which pending wait it satisfies, or how to interpret it, even though
one shared message serves every case: `in_response_to` already uniquely identifies the specific packet a
receiver is holding state for (a `Ping` it sent, or a specific entity message), so whichever side is waiting
already knows, from its own bookkeeping, whether a plain acknowledgment, a `result`, or a `failure` is the
kind of `Reply` it should expect.

## Liveness

`Disconnect` is a graceful, explicit teardown — it says nothing about a peer that silently disappears
(crashes, loses network, etc.) without ever sending it. `Ping` detects that case, and separately measures
round-trip latency for `INetConnection.Latency`, via its `Reply` (`## Responses` above).

- Receiving *any* packet on a connection — not just a `Ping`'s `Reply` — counts as proof the peer is alive
  and resets that connection's idle timer.
- If a side hasn't itself sent anything on a connection for `INetManager.PingInterval` (default 1.5 seconds),
  it sends a `Ping`, guaranteeing the other side receives something at least that often even while the
  connection is otherwise idle.
- If a side hasn't received anything at all on a connection for `INetManager.DisconnectTimeout` (default 5
  seconds — over 3x `PingInterval` by default, allowing for a `Ping` or two lost to UDP), it considers the
  connection lost: `Status` drops to `Disconnected` and `INetManager.Dropped` fires, the same as any other
  connection failure.
- A `Ping` is answered with a plain-acknowledgment `Reply` whose `in_response_to` is the `Ping`'s
  `Packet.sequence`. The original sender computes round-trip latency as the time from sending that `Ping` to
  receiving that `Reply`, and that becomes `INetConnection.Latency`'s new value.

`Ping` and its `Reply` are ordinary post-handshake packets: they carry a real `connection_id` and are covered
by `sequence`/`tag` like any other (`## Packet authentication` above).

## Method delivery

Every controller method call — and `EntityCreate`/`EntityDestroy`, an entity's own lifecycle — is guaranteed
to eventually arrive and be applied, in the order it was sent, despite UDP's own unreliability. This is
unconditional: unlike a property set (`## Property delivery` below), a call is a one-off event, not state to
converge on, so there is no "only the latest matters" option for it. `[NetSecure]` (`### Payload encryption`
above) layers encryption on top of this same guarantee for a specific method, but doesn't change the
guarantee itself.

A method is identified by `(connection, entity_id, method_id)` — one independent channel per direction (a
call is always sent viewer → owner, so in practice this is one channel per calling connection). Entity
lifecycle is identified by `(connection, entity_id)` instead, a channel of its own. A channel allows only one
call in flight at a time: a further call to the same method queues behind whichever one is still
unacknowledged, rather than being dropped. This also gives method calls their ordering guarantee for free —
since the next call on a channel is never even sent until the one ahead of it has been acknowledged, the two
can never arrive, or be applied, out of order.

- Acknowledgment: `EntityCreate` and `EntityDestroy` are acknowledged with a plain-acknowledgment `Reply`
  (`## Responses` above). A `void` method call is acknowledged the same way if it runs successfully, or with
  a `failure`-carrying `Reply` if it's declined or its controller method throws while executing it. A
  `Task`/`Task<T>`-returning call needs no separate acknowledgment: its own `result`/`failure`-carrying
  `Reply` already serves that role.
- Retransmission: if unacknowledged, the sender resends with backoff, the same as `### Retries` under
  `## Connection handshake` above. Each retransmission is a genuinely new `Packet` — its own fresh `sequence`
  and `tag` — so it takes part in `### Replay rejection` exactly like any other packet, with no special-casing
  needed there. An `EntityCall` retransmission carries the exact same content as the attempt before it, since
  it's one specific invocation with fixed arguments; an `EntityCreate` retransmission instead carries the
  entity's *current* property values, not whatever they were at the first attempt, so a slow-to-acknowledge
  create doesn't leave a connection with a stale snapshot once it finally lands. This doesn't complicate
  dedup: since `entity_id` alone (not a per-attempt counter) is what tells a receiver it already knows this
  entity, whichever attempt gets through first is the one applied, and a later, differently-timed attempt that
  also arrives is still just a retransmission of an entity the receiver already has — its content, current or
  not, is never reapplied over what an earlier attempt already established.
- Retry-vs-new discrimination: since a retransmission gets a new `sequence`, `sequence` alone can't tell the
  receiver whether an incoming `EntityCall` is a retry of one it already ran or a genuinely new call —
  re-invoking a controller method that already ran once might have a real effect the second time.
  `EntityCall.operation` (schema in [`Entity.proto`](../Core/Proto/Entity.proto)) exists for exactly this: a
  counter scoped to the channel, incremented once per distinct call sent, left unchanged across
  retransmissions of that same call. A receiver tracks, per channel, both the highest `operation` it has
  *finished* running (with the outcome it computed for it) and, separately, whether an operation is currently
  *in progress* with no outcome yet:
  - An `operation` higher than any it has seen for the channel is genuinely new: it runs it, keeping the
    result to answer with once it completes.
  - An `operation` matching the one still in progress is a retransmission that overtook its own answer; the
    receiver doesn't run it again, and doesn't reply again either — the original invocation's own answer,
    once ready, is the only reply, and it addresses whichever `sequence` was on the packet the answer is
    actually sent in (any later retransmission's), so it still reaches the sender's currently outstanding
    wait.
  - An `operation` matching the last one it *finished* is a retransmission arriving after the fact: the
    receiver doesn't re-invoke anything — it just resends the `Reply` it already computed (whose
    `[NetSecure]` ciphertext, if any, is the same bytes computed the first time — `### Payload encryption`
    above), with `in_response_to` set to whichever `sequence` just arrived.

  `EntityCreate`/`EntityDestroy` need no `operation` field of their own, since each happens at most once for a
  given `entity_id` — which is never reused for a different entity (`## Entities` below) — so a repeat of
  either is unambiguously a retransmission, recognizable from `entity_id` alone.

## Property delivery

A model property set is sent immediately, without waiting for anything — and, unlike `## Method delivery`
above, never queues: a further change to the same property supersedes whatever unacknowledged value was
already in flight for it, instead of lining up behind it. This applies to both directions an `EntitySet` can
travel: an owner's broadcast of a local change, and a viewer's request to change one. A lost value is never
retransmitted as-is, either — once it's superseded, retransmitting it would just be wasted bandwidth, so
losing it outright is fine. What isn't fine is losing the *newest* value too, with nothing left arriving
afterward to correct the receiver's now-stale copy. `Reply` (`## Responses` above) exists to prevent exactly
that:

- The receiver acknowledges every `EntitySet` it accepts — with a plain-acknowledgment `Reply`, or a
  `failure`-carrying one if a viewer's requested change is declined (`## Entities` below) — with
  `in_response_to` set to the packet's `Packet.sequence`.
- The sender tracks, per property per direction (identified the same way `## Method delivery` above
  identifies a method), the `Packet.sequence` of the most recently sent value, and whether an acknowledgment
  covering it has arrived — one for any later `sequence` on that same direction counts too, since `sequence`
  is monotonic and a later one implies the earlier was also received.
- If a property's most recently sent value is still unacknowledged after `INetManager.PingInterval` (the same
  interval `## Liveness` above already uses), the sender resends it — using whatever value the property holds
  *now*, not the one from the original attempt, since a newer change may have superseded it since. What gets
  retried is always the property's current value, never a backlog of past ones.
- Once an acknowledgment covering a property's latest sent `sequence` arrives, it goes idle until the property
  changes again.

Because a property set never queues, more than one value for it can be sent before the first is acknowledged,
so `sequence` is enough to identify which attempt an acknowledgment covers — `EntitySet` has no `operation`
field, unlike `EntityCall`, since it has no retry-vs-new distinction to make: reapplying the same value twice
is harmless.

An entity's initial property values are already carried by its own `EntityCreate` (`## Entities` below), so
this only matters for a *later* change — but since a property set is sent immediately with no ordering tie to
anything else, a change made right after an entity becomes visible could otherwise reach a connection before
its own `EntityCreate` does (`EntityCreate`/`EntityDestroy` are on their own guaranteed, ordered, single-
outstanding channel — `## Method delivery` above — which a same-packet-fast `EntitySet` isn't bound by at
all). To make that impossible rather than just unlikely, an owner doesn't start a property's channel to a
given connection until that connection has acknowledged the entity's `EntityCreate`: any change made before
then is simply reflected in the current values that `EntityCreate`'s own retransmissions carry
(`## Method delivery` above), and a property's own channel — and any `EntitySet` on it — only exists from
that acknowledgment onward.

## Entities

Once a connection exists, `Packet.payload` can also carry one of four entity messages (schemas in
[`Entity.proto`](../Core/Proto/Entity.proto)) — plus the shared `Reply` (`## Responses` above), for whichever
of them expects an answer — used to synchronize [net entities](Api.md#entities) between an entity's owner and
each connection it is visible to. `entity_id` in every one of these messages is scoped to the single
connection it was sent on — it is the id that connection was assigned for the entity, not a global identifier,
and a different connection viewing the same entity uses its own, independently assigned `entity_id`.

Any of these messages — other than `EntityCreate` itself — that names an `entity_id` its recipient doesn't
currently recognize as a live entity is simply ignored. Each entity's channels (`## Method delivery`/
`## Property delivery` above) run independently of each other and of the entity's own lifecycle, so it's
possible, though not common, for something already in flight for an entity to arrive just after that same
entity's `EntityDestroy` has already been processed; ignoring it outright, rather than trying to interpret it,
is what a receiver does with any traffic for an `entity_id` it has no current meaning for.

- `EntityCreate` is sent when a local entity becomes visible to a connection for the first time —
  including when it newly enters range under position-based visibility (`Docs/Api.md#position-based-visibility`),
  having already been in a group the connection is a viewer of. It carries the `entity_id` this connection
  will use for the entity from now on, plus `controller_id`/`model_id`, each naming its interface type via
  `new_controller`/`new_model` the first time that id is used on this connection, and the entity's non-default
  model property values. Later entities sharing a controller or model interface type already introduced to
  this connection leave that type's `new_controller`/`new_model` empty and reuse its id, so each interface's
  full type name is sent at most once per connection.
- `EntityDestroy` is sent when an entity stops being visible to a connection (removed from its group, the
  connection removed as a viewer, or filtered out by position-based visibility — `Docs/Api.md#position-based-visibility`
  — falling out of range). Its `entity_id` is never reused for a different entity afterward (below).
- `EntitySet` changes a single model property, naming it via `property_id`/`new_property` the same way
  `EntityCreate.properties` does (only the first time `property_id` is used, for this entity's `model_id`, on
  this connection). An owner sends it to broadcast a local change to every connection the entity is visible
  to (a property's getter is always synced to every connection the entity is visible to — there is no
  restricting who receives it). A viewer sends it back to the owner to request a change to a property whose
  setter has `NetAccessAttribute` access; the owner applies it locally, broadcasts its own `EntitySet` for
  the new value to every other connection the entity is visible to (the same as any other local change), and
  replies to the requesting viewer specifically with a `Reply` (`## Property delivery` above) — a plain
  acknowledgment if applied, or a `failure`-carrying one if declined.
- `EntityCall` invokes a controller method, carrying the `method_id` this connection will use for the method
  from now on, `new_method` naming it (only the first time `method_id` is used on this connection), its
  positional arguments, and `operation` (`## Method delivery` above). A `Task`/`Task<T>`-returning method gets
  a `result`/`failure`-carrying `Reply` (`## Responses` above) whose `in_response_to` is the `Packet.sequence`
  the `EntityCall` was sent with, correlating it back to the caller's pending call without a separate id
  scheme. A `void` method gets a plain-acknowledgment or `failure`-carrying `Reply` instead (`## Method
  delivery` above), since the caller isn't waiting on a result.

An owner generates a connection's `entity_id`s sequentially and never reuses one for a different entity, for
the life of the connection — unlike `property_id`/`method_id` (below), which are also never reused but scoped
per interface type rather than per entity, `entity_id` is `uint64` rather than `uint32`, since a long-lived,
high-churn connection has more room to grow through entity ids than through interface types. Never reusing an
id is what lets `EntityCreate`/`EntityDestroy` dedup on `entity_id` alone (no `operation` field, per
`## Method delivery` above) with no further conditions: there is no old entity a given id could still
ambiguously refer to, since no id ever refers to more than one entity in the first place. This also keeps a
`NetSecureAttribute` method's payload-encryption key derivation (`### Payload encryption` above) safe, since
it depends on `entity_id` staying unique to one entity for as long as the connection lives.

Property and method names are interned with the same id-on-first-use design as `controller_id`/`model_id`,
but scoped one level narrower, matching which interface each actually belongs to: `property_id` (on
`EntityProperty`, within `EntityCreate.properties`, or directly on `EntitySet`) is looked up in a table keyed
by (connection, `model_id`) — found from the `model_id` the entity's own `entity_id` was created with — and
`EntityCall.method_id` in a table keyed by (connection, `controller_id`), rather than either being shared
connection-wide. Every model interface type gets its own independent `property_id` table, and every
controller interface type its own independent `method_id` table, so an id under one model/controller type
means nothing under another, even if two different interfaces happen to declare a same-named property/method.

A property's encoded value (`EntityProperty.value` within `EntityCreate.properties`, or `EntitySet.value`),
each `EntityCall` argument, and `Reply.result` are opaque `bytes`; the encoding of an individual property or
argument value, based on its declared CLR type, is out of scope for this document — except that, for a
`NetSecureAttribute` method, its arguments and result carry ciphertext rather than plaintext
(`### Payload encryption` above). A property value is never ciphertext, since `[NetSecure]` cannot be placed
on a model interface property ([Access.md](Access.md#netsecure)).
