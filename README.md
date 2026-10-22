# Si_ServerModUI

The optional dedicated-server companion for **Si_ClientModUI**. It tells compatible clients which commands and mod controls are available, then routes their actions through the server's existing handlers. **Current source version: 2.0.0.** Requires the server's Mono MelonLoader and Si_AdminMod.

## Features at a glance

- Supplies each player with the commands and admin options they can access.
- Supports structured mod controls and player-specific menu choices from compatible providers.
- Returns captured command replies and pushes vote updates to the client menu.
- Sends catalog changes to subscribed clients without ongoing client catalog polling.
- Keeps native command authorization; players without the client mod can continue using normal chat.

Install `Si_ServerModUI.dll` on the **dedicated server**. Players use the separate `Si_ClientModUI.dll` client mod. Menu metadata is configured in `UserData/cfg/Si_ServerModUI/menu.json`.

## Commands

The companion exposes other mods' registered commands rather than adding a gameplay command set. It adds these diagnostics for admins with **Generic** permission:

| Command | What it does |
| --- | --- |
| `!hudtest` | Write your current menu frame under the companion's config folder. |
| `!hudtest /command [arguments]` | Execute a real command through the proxy and echo its captured reply. |

Use the second form with the command you intend to run: it executes the action, including any normal gameplay or configuration effects.

Developers: see the [client/server integration guide](https://github.com/daveyvde-1988/Si_ClientModUI/blob/main/README-DEVELOPERS.md) for provider exports and menu contracts.

<details>
<summary>Detailed documentation and historical release notes</summary>

# Build candidate: Si_ServerModUI 2.0.0
Built against the installed public Mono references and staged with Si_ClientModUI 0.17.0; not installed. Export parsing, player-scoped choices and structured control metadata extend the current bounded transport. Our names, configuration path and SiSquadHUD/2 marker remain. Install the matching client/server pair when replacing the temporary friend's SiModUI/2 pair. See [developer contract](../Si_ClientModUI/README-DEVELOPERS.md) and [validation / remaining live checks](../Si_ClientModUI/STRUCTURED_INTEGRATION_0.17.0.md). Earlier notes below are historical.

# Si_ServerModUI 1.1.0

Developer documentation: [client/server integration and Buy-style menu contract](../Si_ClientModUI/README-DEVELOPERS.md), including an AI implementation prompt. This server version also supports Si_ClientModUI 0.16.0.

Optional Mono server addon for Si_ClientModUI 0.15.0. Ported from DrMuck's server backend, with the current client UI retained. Requires the server's Si_AdminMod and MelonLoader. Players without the client mod continue using ordinary chat.

With the server closed, copy `Mods/Si_ServerModUI.dll` from the server ZIP into its `Mods` folder. Retire any old `Si_SquadHUD_Server.dll` so only one UI companion runs. Do not put this Mono DLL on the IL2CPP client. The optional server is not required for singleplayer or standalone client functionality.

The addon discovers registered player commands, phrases and permitted admin commands. It pushes per-player hashed catalogs/diffs, captures synchronous replies addressed to the invoking player, and pushes vote events. Actual command authorization still runs through Si_AdminMod for every invocation. Broadcast and asynchronous output stays native. Capture overflow also stays native.

Configuration: `UserData/cfg/Si_ServerModUI/menu.json`, created on first startup and reloaded after edits. This retains the friend's labels, module ownership overrides, category/priority, navigation prefix, hidden commands, argument hints and match-control metadata. To reuse an existing friend's configuration, copy it from `UserData/cfg/SquadHUD/menu.json` before starting. It is not copied or overwritten by the installer.

Transport: native packet 126, marker `SiSquadHUD/2`. The marker is intentionally stable for wire compatibility. It disables itself if the game defines that packet type. Only subscribed clients receive addon traffic. Client sends bounded discovery hellos and explicit commands; the server checks catalogs every five seconds and sends changes only. The global outbox allows 160 packets/second, bursts of 16, maximum 8192 queued packets. Commands are serialized per player at 100 ms minimum and four queued maximum. No client heartbeats or ongoing catalog polling.

Admin diagnostic: `!hudtest` writes the caller's frame under this addon's config folder; `!hudtest /command` invokes the proxy and echoes captured output. This executes real commands and should be used deliberately. Nothing invokes it automatically.

Build from the workspace root:

```powershell
dotnet build ClientMods/Si_ServerModUI/Server/Si_ServerModUI.csproj -c Release --ignore-failed-sources
```

Reference path can be overridden with `-p:ServerRoot=<server directory>`. Packaging and optional verified installation are managed by `../Si_ClientModUI/Prepare-Release.ps1` and `../Si_ClientModUI/Install-ModUI.ps1 -Side server`. The client ZIP and server ZIP are separate. Neither has been deployed by this integration task.

</details>
