# Steam-optional port (Epic Online Services backend)

This fork makes Jaket's multiplayer work without Steam by hiding all matchmaking and transport
behind a backend abstraction. Two backends ship in the same plugin:

* **Steam** (default) — wraps the original Facepunch.Steamworks code, behavior identical to upstream.
* **Epic** — Epic Online Services: auth via the Connect interface with a Device ID (no Epic account,
  no login UI), lobbies in a global `jaket:global` bucket, transport over EOS P2P. No self-hosted
  server is required; Epic's relays and lobby service are free.

The backend is selected at startup: Steam when `SteamClient.IsValid`, otherwise Epic. It can be
forced via `BepInEx/config/Jaket.Backends.cfg` → `[General] Backend = auto | steam | epic`.

Versioning: `<upstream version>-eos.<n>` (currently `2.0.0-eos.1`).

## Layout

All new code lives in new files:

| Path | Purpose |
| --- | --- |
| `src/Jaket/Net/Backends/*.cs` | Backend interfaces: `INetBackend`, `INetLobby`, `INetConnection`, `INetListener`, `INetServer`, `INetClient`, plus `NetMember`, `LobbyInfo` and the `NetBackends` selector |
| `src/Jaket/Net/Backends/Steam/` | Steam implementation wrapping Facepunch (lobby, relay sockets, overlay) |
| `src/Jaket/Net/Backends/Epic/` | EOS implementation (device id auth, lobbies, P2P transport) |
| `src/Jaket/Net/ChatChannel.cs` | Chat control channel as packets, shared by both backends |
| `lib/eos-sdk/` | Vendored EOS C# SDK **1.19.1.2** (official zip: `Source/Core`, `Source/Generated`, `Natives/EOSSDK-Win64-Shipping.dll`, third party notices), built as its own `Epic.OnlineServices.dll` with `EOS_PLATFORM_WINDOWS_64` defined |
| `src/JaketEosSpike/` | Standalone spike plugin (acceptance test, not shipped): device id login, lobby create/search, P2P echo |
| `.github/workflows/upstream-drift.yml` | Weekly upstream merge + compile check |

## Upstream files touched and why

Edits were kept mechanical; the `Connection` name is preserved in endpoint files via a
`using Connection = Jaket.Net.Backends.INetConnection;` alias.

| File | Why |
| --- | --- |
| `Jaket.csproj` | exclude `lib/**` and the spike from compilation, reference the EOS SDK project, copy the native dll beside the plugin, version `2.0.0-eos.1` |
| `.gitignore` | ignore nested `bin`/`obj` of the new projects |
| `src/Jaket/Content/PacketType.cs` | new `Chat` packet type (the handler array has 32 slots, 16 used) |
| `src/Jaket/Events.cs` | `OnLobbyInvite` carries a lobby code string, `OnMemberJoin/Leave` carry `NetMember`; the Steam subscriptions moved into the Steam backend |
| `src/Jaket/Net/Endpoints/Endpoint.cs` | `Connection` alias only |
| `src/Jaket/Net/Endpoints/Server.cs` | implements `INetListener` instead of `ISocketManager`; socket creation delegated to the backend; new `Chat` handler that verifies, relays and processes |
| `src/Jaket/Net/Endpoints/Client.cs` | same for `IConnectionManager`; `Connect()` takes no SteamId anymore; new `Chat` handler |
| `src/Jaket/Net/Networking.cs` | the whole `OnChatMessage` block moved to `ChatChannel`; `GetTeam`/`GetColor` take a `uint`; `Send` calls `con.Send` |
| `src/Jaket/Net/LobbyController.cs` | `Lobby` is `INetLobby`; create/join/fetch delegate to the backend; join codes are strings (EOS lobby ids are not ulongs) |
| `src/Jaket/Net/LobbyConfig.cs` | default lobby name uses the backend's `SelfName` |
| `src/Jaket/Net/Entity.cs` | `Id` is no longer `readonly`: the EOS backend learns the local id only after an asynchronous login |
| `src/Jaket/Net/Types/Players/LocalPlayer.cs` | identity comes from the backend instead of `SteamClient.SteamId` |
| `src/Jaket/Net/Admin/Administration.cs` | `#/b` goes through `ChatChannel.Send` |
| `src/Jaket/Input/Movement.cs`, `Input/Emotes.cs`, `World/Gameflow.cs`, `UI/Dialogs/Chat.cs` | `SendChatString` call sites go through `ChatChannel.Send`; `Gameflow.OnDeath` takes a `uint` |
| `src/Jaket/Sprays/SprayDistributor.cs` | `Connection` alias only |
| `src/Jaket/Tools/Tools.cs` | `Tools.Id` (SteamId) removed; the `uint id => Name` extension asks the backend |
| `src/Jaket/Harmony/RichPresence.cs` | rich presence goes through the backend (no-op on EOS) |
| `src/Jaket/UI/Dialogs/LobbyList.cs` | works on backend-neutral `LobbyInfo[]` |
| `src/Jaket/UI/Dialogs/LobbyTab.cs` | visibility switch is one `SetVisibility(level)` call |
| `src/Jaket/UI/Dialogs/PlayerList.cs` | `Lobby.Value.` → `Lobby.` (the lobby is a class now) |
| `src/Jaket/UI/Lib/Bar.cs` | `ProfileButton` takes `NetMember`; the profile overlay goes through the backend |
| `src/Jaket/Plugin.cs` | `NetBackends.Load()` during init |
| `src/Jaket/Version.cs` | version constant `2.0.0-eos.1` |

## Design notes

* **Chat as packets.** The old code abused steam lobby chat for plain chat and `#/d`, `#/s`, `#/v`,
  `#/b`, `#/r`, `#/t` control messages. All of it now travels as a `PacketType.Chat` packet
  (sender uint + UTF-8 text), relayed by the host like every other packet, with the parsing kept in
  one place (`ChatChannel`). This improves the Steam mode too. Side effect: chat sent while a client
  is still loading a level is dropped by the loading gate, and messages are no longer delivered to
  players who haven't finished connecting.
* **Identity on EOS.** The whole codebase keys entities, bans and packets on `uint` account ids,
  and the wire format is unchanged. On EOS the account id of a member is an FNV-1a hash of its
  ProductUserId, so every client derives the same id for any member without coordination; the lobby
  owner validates uniqueness on member join and kicks the newcomer on a hash collision (probability
  for a full 8-player lobby is about 1e-8). This deviates slightly from "the host assigns ids at
  join": a deterministic hash avoids racing the P2P handshake against the attribute propagation.
* **Join codes.** Steam codes are the lobby's ulong id, as before. EOS codes are the EOS lobby id
  string. `LobbyController` treats codes as opaque strings.
* **EOS lobby data.** `LobbyConfig` key-values map to public lobby attributes; writes are batched
  and flushed once per frame by `EpicLobby.FlushPending`. Member display names are member
  attributes (`name`), set by each member. The browser search filters on `client = jaket`.
* **EOS transport.** One socket (`jaket`), reliable-ordered packets on channel 0, a one-byte hello
  on channel 1 bootstraps the connection (EOS connections are established by the first send).
  Packets are capped at 1170 bytes (EOS maximum) with an error log on violation; the largest mod
  packet is a 1029-byte spray chunk, so no fragmentation is needed. Connection requests on the
  server wait up to 8 seconds for the requester to appear in the lobby member list before being
  rejected, since membership notifications can lag behind P2P requests.
* **Social surface.** EOS has no overlay: profile buttons do nothing, "invite friend" copies the
  lobby code to the clipboard, rich presence is a no-op.
* **Update check.** `Version.CURRENT` is `2.0.0-eos.1`, which never equals an upstream release tag,
  so the in-game "outdated mod" notice will point at upstream releases. Treat it as a reminder to
  rebase the fork.

## Epic Games portal registration (one-time, human step)

The EOS credentials in `BepInEx/config/Jaket.Backends.cfg` ship as empty placeholders. To make the
Epic backend work, someone has to register a product (free):

1. Sign up at https://dev.epicgames.com/portal and accept the EOS developer agreement.
2. Create an **Organization**, then a **Product** (e.g. "Jaket").
3. Under **Product Settings → Clients**, add a client with the **GameClient** policy
   (peer-to-peer features, no backend trust). Note the **Client ID** and **Client Secret**.
4. Note the **Product ID**, the default **Sandbox ID** and its **Deployment ID**
   (Product Settings → Sandboxes → Deployments).
5. Under **Epic Account Services** nothing is needed — the mod uses the Connect interface with
   Device IDs and never touches Epic accounts.
6. Fill the five values into `Jaket.Backends.cfg` (section `[EOS]`) on every machine, or bake them
   in as defaults before distributing the fork. The client secret of a GameClient policy is not
   sensitive in the way a server secret is, but treat the portal account itself with care.

Also set `[EOS] PlayerName` — there is no platform to take a display name from.

## Manual testing

The compile-level validation is automated; runtime testing needs two game instances on separate
machines (EOS device ids are per-machine, and two instances on one machine would collide):

1. **Spike first** (`src/JaketEosSpike`): copy `JaketEosSpike.dll`, `Epic.OnlineServices.dll` and
   `EOSSDK-Win64-Shipping.dll` into `BepInEx/plugins/JaketEosSpike/` on both machines, fill the
   credentials in `BepInEx/config/xzxADIxzx.Jaket.EosSpike.cfg`, start both games and watch the
   logs: the first instance hosts a lobby, the second finds it, joins and pings; the host echoes.
   `=== SPIKE TEST PASSED ===` on the client log is the acceptance criterion.
2. **Full mod**: set `Backend = epic` in `Jaket.Backends.cfg` on both machines, host a lobby on
   one, copy the code (the invite button does the same), join by code on the other. Verify: level
   sync, player movement, chat and TTS, death messages, emotes/RPS, sprays, bans (the banned
   player gets kicked from the EOS lobby and cannot reconnect), the lobby browser after making the
   lobby public, and that the lobby closes for everyone when the host leaves.
3. **Steam regression**: with `Backend = auto` and Steam running, repeat a quick smoke test of the
   same list against an upstream client — the wire format is unchanged except for the new chat
   packet, so an upstream host and an eos-fork client will desync only in chat; full compatibility
   exists between two fork clients.

> Note: a fork client in Steam mode still interoperates with upstream for everything except chat
> and the `#/x` control messages (death notices, gameflow start/victory, ban notices, RPS results,
> TTS), which upstream sends via lobby chat and the fork sends via packets. Keep lobbies
> fork-to-fork.

## CI

`.github/workflows/upstream-drift.yml` runs weekly: it builds the EOS SDK and the spike (these
need no game assemblies) and attempts a merge of `upstream/main` to detect drift. The main
`Jaket.csproj` cannot be built in CI because it references the game's `Assembly-CSharp.dll` and
friends via `Path.props`; building the mod itself stays a local step.
