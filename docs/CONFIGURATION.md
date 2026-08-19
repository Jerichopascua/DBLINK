# CBMSB2BLink — Configuration

## Settings reference (`appsettings.json`)

| Section | Key | Meaning | Default |
|---|---|---|---|
| `ConnectionStrings` | `CcrisB2B` | Source DB connection string. May be `DPAPI:<blob>` (see below). | — required |
| | `Cbms` | Destination DB connection string. May be `DPAPI:<blob>`. | — required |
| `Sync` | `SyncKey` | Key identifying this sync stream in `SyncControl`/`SyncRunHistory`. | `BCB_NEW` |
| | `StoredProcedureName` | Source-side proc to call. | `usp_GetBCBNewData` |
| | `BatchSize` | Max rows per page pulled from the source. | `5000` |
| | `CommandTimeoutSeconds` | SQL command timeout for both source and destination calls. | `120` |
| | `MaxRunDurationSeconds` | Whole-run cancellation timeout. | `1800` |
| | `SourceMode` | `Sql` today; reserved for the future HTTP fallback bridge (see ARCHITECTURE.md). | `Sql` |
| | `LockFilePath` | Override for the run lock file. Blank = `%ProgramData%\CBMSB2BLink\run.lock`. | `""` |
| `Email` | `EnableOnFailure` | Send a failure email when a run fails. | `true` |
| | `SmtpHost` / `SmtpPort` / `UseSsl` | SMTP relay settings. | — |
| | `SmtpUsername` / `SmtpPassword` | Only needed if the relay requires auth (most internal relays don't). | — |
| | `From` / `To` | Sender and recipient list. | — |
| `Serilog` | — | Standard Serilog config section (console + rolling file sinks). | see `appsettings.json` |

Config sources, in override order: `appsettings.json` → `appsettings.{Environment}.json`
→ environment variables → command-line args (standard `Host.CreateDefaultBuilder`
behavior). Set `DOTNET_ENVIRONMENT` to select an environment file.

Options are validated at startup (`ValidateDataAnnotations().ValidateOnStart()`); a
missing required value fails fast with a clear message and exit code 1, before any
connection is attempted.

## Protecting connection strings (DPAPI)

Connection strings may be stored as `DPAPI:<base64>` instead of plaintext. They're
decrypted in-memory at startup using Windows DPAPI, `DataProtectionScope.LocalMachine`.

**LocalMachine, not CurrentUser** — the app runs unattended via Task Scheduler, often
under a service account whose profile is never loaded; `CurrentUser`-scoped keys aren't
reliably available in that case. The trade-off: any process running as any account on
that machine can decrypt the value. Treat this as protecting the config file from being
copied off the machine (e.g. in a backup, a git commit, a screen share) — not as a
substitute for OS-level access control on the install directory.

**DPAPI keys are machine-bound** — a value encrypted on one machine cannot be decrypted
on another. Encrypt on the machine that will actually run the scheduled task.

To produce an encrypted value:

```powershell
cd tools
.\Protect-ConnectionString.ps1 -ConnectionString "Server=CBMS_SERVER;Database=CBMS;User Id=svc_cbmsb2blink;Password=REAL_PASSWORD;TrustServerCertificate=True;"
# -> DPAPI:AQAAANCMnd8BFdERjHoAwE/Cl+sBAAAA...
```

Paste the full `DPAPI:...` output into `ConnectionStrings:CcrisB2B` / `:Cbms` in
`appsettings.json`. Plaintext values (no `DPAPI:` prefix) still work — useful for local
development against a dev/test instance.

## Task Scheduler setup

1. Publish: `dotnet publish src/CBMSB2BLink.Console -c Release -r win-x64 --self-contained false -o C:\Apps\CBMSB2BLink`
2. Create a Basic Task in Task Scheduler:
   - **Action**: Start a program → `C:\Apps\CBMSB2BLink\CBMSB2BLink.exe`
   - **Start in**: `C:\Apps\CBMSB2BLink`
   - **Trigger**: e.g. every 15 minutes (match to how fresh CBMS needs to be)
   - **Run whether user is logged on or not** — required for unattended execution, and
     is why connection strings use `LocalMachine`-scoped DPAPI rather than `CurrentUser`.
   - **Settings → Do not start a new instance** — primary defense against overlapping
     runs; the app's own file lock is the backstop.
   - **Settings → Stop the task if it runs longer than** — set a bit above
     `Sync:MaxRunDurationSeconds` as an outer safety net.
3. Verify the run: check exit code (0 = success incl. no-new-data, 1 = failure) and the
   log file under `C:\ProgramData\CBMSB2BLink\logs\`.

Alternates noted in the original spec (SQL Agent CmdExec job, a Windows Service with an
internal timer) both work with this same executable — CmdExec would call the same
published `.exe`; a Windows Service wrapper is unnecessary complexity for something this
short-lived and is not recommended unless there's already service infrastructure to hang
it off of.
