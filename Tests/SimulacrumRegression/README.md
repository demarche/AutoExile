# Simulacrum regression checks

For the latest click/HUD investigation, measured delays, fixes, and the new
`[InputTrace]` log fields, see [InputLatencyInvestigation.md](InputLatencyInvestigation.md).

Run from the plugin directory:

```powershell
dotnet run --project Tests/SimulacrumRegression/SimulacrumRegression.csproj
dotnet build AutoExile.csproj --no-restore
dotnet run --project Tests/NavigationRegression/NavigationRegression.csproj
```

The main project build also copies the plugin to `Plugins/Temp/AutoExile`.
Exit ExileAPI first if Loader.exe holds the destination DLL open.
The tests do not send keyboard or mouse input.

Coverage: missing/final-wave state, concurrent and cancelled input, input timeouts,
cleanup without the overlay synchronization context, observed kills despite new spawns,
boss damage, despawns, stalled damage windows, preservation/consumption of pending paths,
and the Windows mouse INPUT structure layout.

## Evidence from 2026-09-12 logs

- 18:53:52–18:54:10: route requests rose from #1491 to #2037 while the player had no movement input. A completed path was replaced before navigation consumed it.
- 18:56:59–18:57:02: `nearby=0 cached=0`, but W stayed held because path computation enabled targetless channeling.
- 18:57:18 onward: the decision said “moving to next Spark position” while W was held and movement was inactive. Targeted combat input was not suppressed during relocation.
- These logs do not establish the cause of the reported white overlay. Mouse submission now uses Windows input directly; input-gap waits are bounded, and continuation/cleanup does not depend on overlay message processing.

## Live validation

Confirm automatic fragment insertion and activation, movement from the entrance to the
monolith, and relocation when nearby targets stop losing HP/ES. Damage to a living boss
counts as progress; a missing entity is not counted as a kill. Low-ES stationary recovery
continues while damage is being dealt; stalled recovery permits a short relocation, with
defensive casting restored if movement fails for four seconds.

Existing ExileAPI `Logs/InfoYYYYMMDD.log` receives `[MapDevice]`, `[InputSequence]`,
`[Simulacrum][MonolithState]`, `[Simulacrum][MonolithClick]`, and
`[Simulacrum][SparkReposition]` records. Input errors, durations, submission/release events,
and relocation reasons distinguish an input problem from navigation or combat decisions.
Windows accepting an input event is not proof of a game action: map-device and wave-state
transitions remain the confirmation.
