# App integration regression probe

Run from the repository root on Windows:

```powershell
dotnet run --project tools/AppIntegrationProbe/AppIntegrationProbe.csproj -c Release
```

Requires the existing .NET 10 SDK, restored App dependencies and an installed FFmpeg executable. The project references the current App source through a relative ProjectReference. It builds assemblies without launching the desktop App.

## What it actually tests

The probe invokes the real MainViewModel private methods by reflection. It skips MainViewModel's constructor and injects an isolated fixture so SettingsStore.Load/Save and the production ProductLog path are never used. A temporary ProductLog writes only to a newly created GUID directory under the system temp directory.

| Scenario | Actual assertion |
| --- | --- |
| Authenticated delayed MEDIA_READY after shutdown | No sharing state, no pending state, no process; later Start ignored |
| Stop while awaiting READY, then delayed READY | Cancel revokes pending start; subsequent new start works |
| New pending Start supersedes an old generation | Old READY cannot clear new pending state or start a process |
| Same-key Peer changes port, capability and inventory | Record and selected Peer updated while selection stays on the same computer |
| Edit next-start media port | Active discovery and receive port stay aligned; wrong active-port source binding rejected |
| Permanently pending initialization during shutdown | Audio cancellation prefix executes before first await; initialization wait is bounded |

Each control responder uses a random loopback UDP port and a new random in-memory shared key. Keys are never written or printed. Discovery is constructed but never started, so there are no LAN advertisements or production discovery listeners.

Successful start paths use MediaStreamSession's internal Process factory to replace device arguments with a real FFmpeg **16×16 lavfi black synthetic source**, writing to the null muxer. They do not capture a camera/microphone, play audio or send a media UDP stream. Synthetic process IDs are tracked and awaited after shutdown. No GUI or TCC action is taken.

The deliberately late UDP reply can elicit a Windows ICMP connection reset once its ephemeral destination has closed. The test responder ignores only this expected SocketError.ConnectionReset while retaining its request deadline.

## Verified / unverified

- Verified on Windows on 2026-10-02: all six scenarios above, using the current source build. See [stdout](evidence/windows-2026-10-02.txt).
- Not run on Mac: the probe explicitly rejects other platforms. It does not prove Mac native permissions or cross-platform UI lifecycle.
- Does not replace GUI close/resize/transparent-corner tests, physical 4K camera consumption, audio endpoint switching, keyboard injection, or network fault/lease tests. Those have separate probes and runtime gates.
- Uses private fields as fixture seams. If MainVM adds a mandatory resource field or renames a seam, update the fixture explicitly; do not call its real constructor as a shortcut.
- Source methods are tested; actual installed production binaries must be rebuilt and deployed before claiming the regression is resolved for the user.
