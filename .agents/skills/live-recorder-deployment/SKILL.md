---
name: live-recorder-deployment
description: Use an agent harness to install, upgrade, or recover the Yohuke Live Recorder server on a computer or an existing server. Handle deployment work through available tools and guide users with no technical experience through browser setup and verification. Use when a user wants to get Live Recorder running or update an existing installation.
---

# Deploy Live Recorder

Use the agent harness to do the technical work on the intended machine when it has access. Speak the user's language. Give one instruction at a time for anything the user must do, with the exact window or page, button, and expected result. Wait for that result before continuing. Explain choices in terms of where the site will open, whether it will keep running, and what could happen to existing recordings.

## Identify the destination and scope

1. Establish where Live Recorder should run and who needs to open it. If unclear, ask whether they mean **this computer** or **a server they already have**. For a first local test, default to access from that computer only.
2. Verify that the harness is operating on the intended machine. Use an existing authorized connection for a remote server. If the harness runs in a cloud workspace or sandbox, establish how it can reach the user's machine before installing anything. If it lacks access, guide the user to open the harness on the intended machine or connect it through their existing server access. Give platform-specific manual instructions only when the user needs them.
3. Inspect the operating system and CPU architecture, available storage, media tools, MongoDB, and the application's port and service. Check for an existing process, settings file, data directory, or recording directory before selecting a fresh install. Explain any downtime before stopping an existing instance. Treat internet access and provisioning paid services as separate user decisions.

## Use the supported deployment path

Use the repository [quick start](../../../README.md#快速部署) for the release package and [deployment documentation](../../../Sources/README.md#部署与配置) for service setup, storage, HTTPS, backup, and recovery. Check the release notes for the chosen version before upgrading.

1. Inspect the actual assets in [official releases](https://github.com/Yohuke-no-Symphony/LiveRecorder-Neo/releases) and select the stable `server-*.zip` for the destination's operating system and CPU. Respect a version the user specifies. The server archive includes the .NET runtime and WebUI. If a matching asset is unavailable, check the documented build targets for the requested version. Use a source build only when the target is supported and the build fits the task. Explain any remaining platform or release access blocker. Set up separate Workers only when requested.
2. Check that the target has a supported MongoDB 8.x instance and working `ffmpeg` and `ffprobe` executables. Reuse suitable dependencies. For missing ones, determine the supported installation method for the actual operating system and configure the media tools for the account that will run Live Recorder. Keep MongoDB private.
3. Extract the full archive into a permanent program directory. Keep `webui/` and `appsettings*.json` with the executable. Use an explicit, writable `settings.json` path and persistent data and recording directories with enough space. For a fresh private install without remote Workers, launch from the program directory. The extra setting disables the separate transcoding control port, which can otherwise listen on the network even when the web address is local.

   ```sh
   chmod +x ./Yohuke.LiveRecorder.Server
   ./Yohuke.LiveRecorder.Server --urls http://127.0.0.1:4784 --config '/absolute/path/to/settings.json' --TranscodingControl:Enabled false
   ```

   On Windows, use PowerShell.

   ```powershell
   .\Yohuke.LiveRecorder.Server.exe --urls http://127.0.0.1:4784 --config 'C:\path\to\settings.json' --TranscodingControl:Enabled false
   ```

   Replace the settings path with the one verified on the target. Use a free web port or keep the existing deployment port. For a server, give the user a browser URL they can open through a private connection, a forwarded port, or the configured HTTPS site. The server's loopback address is only reachable on the server itself.

4. Match how the server runs to what the user asked for. For continuous use on Linux, adapt the [systemd example](../../../Sources/deploy/live-recorder.service) to the actual account and paths, carrying over the intended network settings. Verify that startup is enabled and the service is running. On other systems, inspect the supported startup mechanism or explain whether the app currently depends on a running session. For authorized public access, follow the project HTTPS reverse proxy instructions for trusted proxy addresses, WebSockets, and media playback. Keep the first-time setup page private and verify the active listeners match the requested access.

## Finish in the browser

- Open the verified URL and confirm that it shows the Live Recorder setup or sign-in page. For a fresh install, have the user enter the installation token from the first-start log directly in the setup page. Show them how to reach that log on their platform. Keep tokens, passwords, connection strings with credentials, and private keys out of chat and screenshots. Use the app's fields or an available secure input mechanism for secrets.
- For **New installation**, guide the user through **Setup mode**, **Administrator**, **Storage**, and **Database** in that order. The administrator chooses their own password in the page. Supply the verified data folder, recordings folder, media tool paths, and connection details for an empty database. **Finish setup** opens **Sign in**. Use the installed version's language and on-screen labels when guiding the user.
- For **Restore instance**, use the original database, storage paths, and administrator account. Follow the repository recovery instructions and have the user sign in with the existing account after setup.
- Confirm sign-in and the main application page after setup. If the request includes recording, guide the user through Bilibili sign-in and adding a room. Let the user complete login or QR scanning in the app. Verify capture only if a room is live and the user requested a recording check.

## Protect existing installations

For an upgrade or move, identify the current version, service account, service definition and overrides, active settings, database, and storage paths. Record a known playable recording for verification when one is available. Preserve local `appsettings*.json` overrides such as proxy and Worker listener settings when applying the new release's defaults. If the old launch uses `--DataDirectory` or `LIVE_RECORDER_DATA_DIR`, prepare the documented `--config` migration and preserve the original data directory, bootstrap files, and keys.

Stage the new package and confirm backup capacity and the rollback path before downtime. Run only one version against the production database and recording directories. Stop recording and the service for a consistent backup of `settings.json` if present, the data directory (including `keys/`), the recordings directory, and MongoDB. Verify the backup before replacing program files. Preserve the Windows service account and its DPAPI environment where applicable. If backing up fails after the service stops, restart the unchanged instance. If the upgrade fails, restore the previous program and configuration, and restore data only as required by the established rollback plan. After the upgrade, confirm sign-in, existing rooms, and playback of the known recording. If the setup wizard appears unexpectedly, stop and check the active settings and storage paths before proceeding.

## Verify and report

Check service state, the browser response from where the user will open the site, and setup or sign-in as applicable. If a step fails, use the failing step and redacted logs to check the relevant port, permissions, database connection, or media tool before retrying. Leave a failed installation in a known state and explain what action is needed in plain language.

Report the verified address, installed version, and whether recording can continue after the user closes the harness or restarts the machine. Name any remaining action, such as completing the setup page or adding a room. Call the service ready for use once the user can reach the signed-in application.
