# Jedar Bridge

Company remote support bridge. A Windows administrator client connects outward over HTTPS/WebSockets. Your operator uses a private API token and the client's six digit code. Codex runs on your computer and uses scripts/bridge.ps1. No Codex install or login is needed on the remote computer.

## First use

1. Start the server: `powershell -File scripts/start-server.ps1`.
2. Configure your existing Cloudflare Tunnel: public hostname `support.example.com`, HTTP service `http://127.0.0.1:4353`. The cloudflared process must run on this computer. For Docker, use the host address instead of container localhost.
3. Check https://support.example.com/health for `service: JedarBridge`.
4. Give the remote user `dist/Client/Bridge.Client.exe`, or download it from https://support.example.com/download/client.
5. The remote user opens the EXE, accepts the Windows administrator UAC prompt, checks the server name, and types `ALLOW`.
6. Pair from your computer using the displayed code:

       .\scripts\bridge.ps1 -Action pair -Code 123456

7. Use the returned deviceId:

       .\scripts\bridge.ps1 -Action exec -Device DEVICE_ID -Command 'hostname; whoami; Get-PSDrive -PSProvider FileSystem'

Give Codex [CODEX-BRIDGE.md](CODEX-BRIDGE.md), the device ID, and the issue. The script reads the operator token locally. Do not paste the token into chat or give it to the remote user.

If Cloudflare returns 502, check the origin scheme. This server speaks HTTP locally, so the tunnel service must be `http://localhost:4353` or `http://127.0.0.1:4353`, not HTTPS. The public address remains HTTPS.

Double-clicking the client uses https://support.example.com. To change servers or test locally:

    .\Bridge.Client.exe https://support.example.com
    .\dist\Client\Bridge.Client.exe http://127.0.0.1:4353

## Capabilities

PowerShell runs as the administrator who approved UAC. Inspect processes/services/logs and list/read/write/delete files. Access respects Windows permissions and protected system processes; it is not SYSTEM access. Interactive desktop control and interactive terminal prompts are not implemented.

The connection is realtime. Command results return when the command completes; stdout is not streamed live. Each job starts a new PowerShell process, so variables do not persist. Supply `-Cwd` as needed. One job runs per device. Command timeout: 1-600 seconds. File transfers: 4 MB. Command output: 200,000 characters per stream. File API deletion supports files and empty folders; recursive work requires an explicit command.

The client remains visible. Closing it or pressing Ctrl+C ends access. To revoke:

    .\scripts\bridge.ps1 -Action revoke -Device DEVICE_ID

Sessions last up to 8 hours. Unclaimed codes expire after 10 minutes. Disconnects require reopening the client, consent, and a fresh code. No automatic startup, service install, or unattended reconnect. Commands that deliberately launch independent services or scheduled work can outlive the session.

## Administration

The server creates `dist/Server/data/operator-token.txt`. On Windows its data directory is restricted to its account, SYSTEM, and administrators. Every holder of this token has operator access. A code alone cannot grant access. Keep the listener behind HTTPS via Cloudflare Tunnel.

The `audit.jsonl` file records pairing, operation type, job ID, completion, and disconnection. It omits command bodies and file contents. Job results remain in memory (latest 100); restarting clears jobs and sessions. This initial release has one operator role. Multi-user login, persistent enrollment, reconnect, signed updates, and an installer are future work.

The start script runs the server in the background. Logs: `server.log` and `server-error.log`. PID: `dist/server.pid`. Stop the process matching this PID and the full `dist/Server/Bridge.Server.exe` executable path using Task Manager. Restart with `scripts/start-server.ps1`. To rotate the token, stop the server, remove only `data/operator-token.txt`, and restart.

To move to another Windows x64 server, copy `dist/Server` and `dist/Client` preserving their sibling layout, and run `Bridge.Server.exe`. Its data directory defaults to the directory alongside the executable. Linux hosting requires publishing for the matching Linux runtime.

Environment settings:

- `BRIDGE_LISTEN`: default http://127.0.0.1:4353.
- `BRIDGE_DATA`: data directory.
- `BRIDGE_TOKEN`: optional override for the operator script; the server uses its token file.
- `BRIDGE_TEST_NONADMIN=1`: tests can run the client DLL without elevation. This cannot grant privileges; the published EXE always requests UAC.

## Build and test

Requires .NET 10 SDK. Published EXEs include their runtime; target computers need no .NET installation. These builds target Windows x64.

    .\scripts\build.ps1
    & "$env:LOCALAPPDATA/JedarBridge/dotnet/dotnet.exe" build JedarBridge.sln -c Release
    python tests/integration.py

The test uses port 14353, temporary data, and real client/server DLLs. It checks authentication, one-time pairing, commands, file CRUD, overwrite protection, invalid requests, concurrency, timeouts, output limits, and revocation. UAC dialogs and desktop interaction require manual testing. EXEs are unsigned; Windows can display an unknown-publisher warning.

References: [Cloudflare Tunnel setup](https://developers.cloudflare.com/tunnel/get-started/), [Cloudflare WebSockets](https://developers.cloudflare.com/network/websockets/).
