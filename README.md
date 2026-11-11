# Local InnerNet Server

A loopback-only replacement for the decompiled Among Us client build whose `Constants.GetBroadcastVersion()` returns `36947700` (dated `2018-11-14`). It replaces the retired service for private, same-machine play; it does not connect to official services, as those are no longer up and have no working reverse-engineered ports online.

The client build is authoritative for match simulation. This server implements the expected Hazel UDP connection handshake, multiple simultaneous lobbies with unique four-letter game codes (GameCode V1), lobby listing, join-order host migration, player limits, kick/ban handling, game start/end messages, and the broadcast/targeted game-data relay used by the client. Only the current room host can start the game, change room visibility, or send the host-only settings RPCs (`SyncSettings` and `SetInfected`); the host-only check identifies the target PlayerControl object so MeetingHud’s overlapping `CastVote`/`ClearVote` call IDs still relay correctly. Rooms are private by default and appear in Online matchmaking only after the host makes them public. Joining clients receive the room’s current visibility state, and host privacy changes are broadcast to everyone in that room as expected.

After a game ends, the room code and its privacy setting remain reserved. Clients who choose **Play Again** wait for the host’s request; when the host joins again, the server restores the lobby roster under the same code and broadcasts the retained visibility. A host leaving a lobby is replaced by the earliest remaining player. During a match, the server reads the client’s `SetInfected` RPC and player death-state updates; if all impostors are gone, it ends the game with `ImpostorDisconnect`, and if living crew fall to parity with living impostors, it ends with `HumansDisconnect`. In this 2018 client, `EndGameManager.SetEverythingUp` returns early for both disconnect reasons before drawing the winning-player sprites. The server sends the end-game reason, but the missing sprites are a client-side behavior; the patcher intentionally leaves game logic and UI unchanged. Existing game-started and game-full join rejection responses are preserved. Ctrl+C sends disconnects to connected game clients before shutting down the UDP listeners. A local telemetry sink is available, but the client patch does not change the telemetry URL or ad behavior.

## Build and run

Requires the .NET 10 SDK. From the workspace root:

```sh
dotnet run --project LocalInnerNetServer
```

By default the server binds only to `127.0.0.1`: game UDP `22023`, announcement UDP `22024` (not implemented), and telemetry HTTP `22025` (not implemented). `--port` changes only the game UDP port. The client expects game traffic on its configured port (normally `22023`).

## Redirect the client

The decompile hardcodes the retired game-service IP. The patch command changes only those exact game-service address literals to loopback so the client can connect to this server. It does not modify gameplay, the UI, telemetry, or ad behavior, and writes a *separate* assembly; it never overwrites the original. You'll need your own legally obtained copy of Assembly-CSharp.dll from the Managed folder inside the Data folder for Among Us to make this work as follows:

```sh
dotnet run --project LocalInnerNetServer -- patch-client "/path/to/Among Us_Data/Managed/Assembly-CSharp.dll"
```

The output is `Assembly-CSharp.local.dll` beside the input. Back up the original `Assembly-CSharp.dll`, then use the generated file (renamed to `Assembly-CSharp.dll`) in the game copy you control. Keep `Assembly-CSharp-firstpass.dll` alongside it. To undo, restore your backup.

Run the patched client, choose **Online**, and host a game. To make it visible in the local lobby list, use the in-game public/private toggle; another patched client can then join from that list. You can run separate game copies on the same machine.

## Validation

```sh
dotnet run --project LocalInnerNetServer -- self-test
dotnet run --project LocalInnerNetServer -- integration-test
```

The integration test exercises Hazel handshakes, concurrent game creation with unique codes, public/private matchmaking transitions and per-client visibility updates, targeted vote/clear-vote RPC relay, host migration, waiting-for-host and same-code Play Again behavior, impostor/crew disconnect end reasons (including a sole impostor host), started-game join rejection, rejection of non-host start/privacy/settings changes, authorized host settings/start, gameplay relay, the local announcement response, the telemetry sink, and active-client disconnects during shutdown.

## Scope

This is deliberately restricted to loopback and the decompiled `36947700` protocol. It does not provide matchmaking outside this server process, internet/LAN hosting, or server-authoritative gameplay; those are not required by this client flow. It does not rewrite user-clicked social links. Do not expose the ports or use it with a different build without checking that build's protocol and endpoint behavior.

## Security

This project is created as a Proof-of-Concept (PoC) and reimplementation of the 2018 client's server, which is not available anywhere else on the internet (the Impostor project starts at versions 2020+ and excludes 2018-2019). It's unsafe and many requests are not validated, sanitized or dropped before being sent. Therefore, be aware that if you plan on publicly exposing this to the internet in any way, you'll need to create your own Anti-Cheat.


## Stability

The project is tested entirely locally, and therefore might not check for any quirks or bugs that could occur in real online play. I do not plan on maintaining this repository, so if you find any bugs or issues, fix them via pull requests yourself (NOT issues!), and I'll merge any important ones.