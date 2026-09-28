# SQL Workbench

[Project page](https://imgsn.github.io/sql-workbench/) · [GitHub repository](https://github.com/imgsn/sql-workbench)

ASP.NET Core 10 MVC workbench for an internal development team. No sign-in.
Connect to SQL Server, inspect metadata, compare schemas and rows, and download
schema or INSERT scripts. The application never applies generated scripts.

## Run

```powershell
$env:ASPNETCORE_ENVIRONMENT='Development'
dotnet run --project Workbench.csproj --no-launch-profile --urls http://localhost:5180
```

- Live workspace: http://localhost:5180/?tool=connections
- Sample-data mode: http://localhost:5180/?tool=schema&demo=true

Use SQL Server or Windows authentication with SELECT on relevant tables and VIEW
DEFINITION on the database. Paste a connection string or use the connection form.
Windows authentication accepts `Integrated Security=True`, `Integrated Security=SSPI`,
or `Trusted_Connection=True`, without a SQL username/password. It uses the identity
running the application, not the browser user's Windows identity. Locally this is
the account running dotnet; on IIS configure the application identity with database
access. A Linux Docker container needs its own Kerberos configuration to use
integrated authentication; it does not inherit the Windows host's credentials.

For example:

```text
Data Source=.;Initial Catalog=Taiqz_backup;MultipleActiveResultSets=True;App=EntityFramework;Encrypt=False;Integrated Security=True;Pooling=True;Max Pool Size=500
```

Pasted encryption, MARS, application name, pooling, and maximum pool size settings
are preserved. Encryption defaults to enabled when omitted. For a self-signed certificate,
explicitly choose Trust server certificate (or set it in the connection string).
Credentials are protected in server memory, bound to an HttpOnly session cookie,
and expire after two hours by default. Disconnect removes them. They are never
written to the application database or returned in connection-list responses.
Sessions and connections expire on restart; there is no durable credential store.

### Cookie encryption keys

Data Protection keys persist separately from database credentials. Local runs use
`App_Data/DataProtectionKeys` under the content root. The directory is excluded from
Git, publishing, and Docker build contexts. Windows protects key material with
DPAPI for the account running the application. Keep that account consistent across
restarts. The application name is fixed as `SQLWorkbench`.

To choose a persistent location outside the deployment directory, set
`DataProtection__KeysPath`. Under IIS, grant the application identity read/write
access to that directory and enable **Load User Profile** for DPAPI. Keep the key
directory across deployments. Do not copy a Windows user's DPAPI keys to a different
account or machine; use an appropriate shared key-protection provider for that setup.

Session and antiforgery cookies use versioned names to ignore cookies from the old
ephemeral-key preview. Reload existing browser tabs once after updating. Connection
credentials and session contents remain in memory and must be re-entered after a
restart, even though the cookie encryption keys remain valid.

## Implemented tools

- **Connections:** test/add/list/disconnect; SQL and Windows authentication; session isolation;
  server version detection; optional server allowlist; maximum 10 per session.
- **Explorer:** tables, views, procedures, functions, and table triggers; columns,
  keys, indexes, defaults, computed expressions, and module definitions.
- **Schema compare:** whole databases, named schemas (including differently named
  schema mapping), or selected source objects; names, types, combined, and detailed
  modes; exact-name matching; detailed property inspection; JSON report export.
- **Data compare:** independently selected tables, composite matching keys, explicit
  column mappings, optional WHERE predicates, case/whitespace rules, changed-cell
  display, filters, pagination, and JSON export. Duplicate/NULL keys are rejected.
- **Schema scripts:** SMO CREATE scripts with keys/constraints, indexes, triggers,
  optional dependencies, GO separators, preview/copy, and UTF-8 .sql download.
  Without GO, individual SMO batches use EXEC to preserve CREATE batch boundaries.
- **INSERT generator:** one SELECT/CTE query, preview, explicit destination column
  mapping, exact numeric/Unicode/binary/date literals, exclusion of generated
  columns, optional identity values, transaction wrapper, 1–1000 rows per statement,
  preview/copy, and UTF-8 .sql download. Destination metadata comes from a table in
  the selected connection; files can be used on a matching destination database.
- **Shared comparison profiles:** optional EF Core SQL Server storage for names,
  comparison modes, and schemas. Credentials and query results are never stored.
  Without the application database, profiles can be downloaded as JSON. Import is
  not implemented. Shared profiles intentionally belong to the whole internal team.

All live controls call the MVC API; demo fixtures are used only with `demo=true`.
Long requests can be cancelled. Database operations use a 30-second command timeout.
The default result limit is 5,000 rows and 8 MB of materialized values; oversized
results fail explicitly instead of returning a misleading partial comparison/export.
Use WHERE/TOP for larger tables. The browser previews the first 100 query rows.

## SQL Server compatibility and scope

Validated end-to-end against local SQL Server 2022 (16.0). Metadata uses a SQL Server
2008+ catalog baseline and detects newer generated-column metadata. Actual older
server connectivity depends on SqlClient TLS/protocol support; not every historical
version is guaranteed. SMO scripts target the source version. Unsupported types or
server features produce errors rather than fabricated scripts.

Comparisons cover user database objects and listed properties, not logins, jobs,
permissions, storage/partition settings, or every advanced SQL Server feature.
Encrypted definitions cannot be fully inspected. Schema matching is ordinal and
case-sensitive. Automatic rename detection and manual object-renaming maps are not
implemented; data column mappings and named-schema mappings are supported.
Reads of two databases are separate observations, not one cross-server snapshot.
Query preview and INSERT generation are separate reads, explicitly identified in
the UI. Constraints and destination conversion errors are checked when the exported
file is executed externally. SQL variant and CLR/spatial result types require a
supported explicit CAST. Numeric tolerances and scheduled/background jobs are not
part of this release. Exports currently use one SQL file, not per-object ZIP files.

## Optional EF Core application database

The main tools need no application database. To enable shared profiles:

1. Create a dedicated SQL Server database for workbench settings.
2. Run `Data/setup.sql` manually against that database.
3. Configure `ConnectionStrings__Workbench` through environment variables or a
   secret provider. Do not commit its credentials.

```powershell
$env:ConnectionStrings__Workbench='Server=...;Database=WorkbenchSettings;User Id=...;Password=...;Encrypt=True'
```

Only this configured database receives EF Core profile writes. Schema setup is
never run automatically, and user-connected databases receive no workbench writes.

## Hosting

### IIS

Install the .NET 10 ASP.NET Core Hosting Bundle. Publish:

```powershell
dotnet publish Workbench.csproj -c Release -o publish
```

Point an IIS site at the publish directory with an application pool set to
No Managed Code. The Web SDK generates web.config. Configure HTTPS at IIS and
restrict network access to the internal team. No application sign-in is provided.
Use one worker process because sessions and credentials are in process memory.
Recycle/redeploy requires reconnecting. Deployment to an IIS site was not performed.

### Docker

```powershell
docker build -t sql-workbench .
docker run --rm -p 127.0.0.1:5180:8080 -v workbench-keys:/var/lib/workbench/keys sql-workbench
```

Put an HTTPS reverse proxy in front for shared access. Use a single instance;
connections are not shared across replicas. On Docker Desktop, a SQL Server on the
host is normally reached through host.docker.internal, not localhost. Add that name
to the configured allowlist if used. Container execution requires a running Docker
engine; it was unavailable during this implementation.
The image stores cookie keys at `/var/lib/workbench/keys`; the named volume keeps
them across container replacements. Restrict access to this volume. Linux does not
use Windows DPAPI; protect the volume at rest using the hosting environment.

### Configuration

`Workbench` in appsettings.json controls MaxRows, CommandTimeoutSeconds,
ConnectionLifetimeMinutes, AllowedServers (exact server/instance strings), and
AllowWindowsAuthentication (true by default). AllowedServers empty permits users
to choose any SQL Server reachable by the host. Connection strings are rebuilt
from supported fields, so file attachment, alternate failover hosts,
and arbitrary connection-string settings cannot be enabled by a pasted value.
Disconnecting or pruning an expired connection also clears its SqlClient pool.

The SELECT parser rejects writes, multiple statements/batches, SELECT INTO,
sequences, external/cross-database references, variables, and query hints. Use
read-only database credentials as the database-level enforcement. API mutations
require an antiforgery token. SQL errors are sanitized; query text and credential
values are excluded from application logs. Request concurrency is bounded.

## Validation

### Export data differences

After running **Data compare**, use **Export data differences** to select rows, preview their SQL, and download `data-differences.sql`. Source-only rows generate INSERT, changed rows generate UPDATE of the changed mapped columns, and target-only rows generate DELETE. The target database/table and mapped target column names are used throughout. The app only reads metadata/data and never executes the generated statements.

Select all mandatory destination columns for inserts, or provide destination defaults. Enable **Preserve identity values** when selected inserts map an identity column. Computed/generated columns are omitted from inserts and cannot be updated. Matching keys cannot be changed by this export. Composite keys, NULL values, Unicode, binary data and exact decimal/bigint values are supported. Text/ntext/image/XML matching keys are rejected because they do not support ordinary SQL equality.

The server rereads the comparison and rejects exports if compared data, table metadata or settings changed. SQL contains target-key uniqueness and original-value guards, a transaction, explicit rollback on conflict, and identity cleanup. String guards detect case and trailing-space changes. Matching rules may pair differently formatted source/target keys; updates/deletes use the original target key and preserve it. Export selection is independent of the results display filter.

Review filters, unselected/unmapped columns, conversions, foreign keys, triggers/cascades and operation ordering before using a file outside Workbench. Reads are separate and are not a cross-database snapshot. A draft is not an automatic deployment. Execute only in a fresh session outside an existing transaction. The generated TRY/CATCH explicitly rolls back because [RAISERROR does not honor XACT_ABORT](https://learn.microsoft.com/en-us/sql/t-sql/language-elements/raiserror-transact-sql?view=sql-server-ver17).

### Schema difference drafts

Run **Schema compare**, select changed objects in **Generate SQL from differences**, and choose **Generate migration draft**. The direction is always source → target: statements change the target to match the source. Review the risk table, then copy or download `schema-differences.sql`. Demo mode offers the same workflow using fictitious data.

Supported drafts include ordinary column additions/removals, type/nullability/collation changes, default replacements, whole-object drops, and SMO creation scripts for source-only objects. Narrow comparison modes preserve target column settings outside their scope. Before returning a live draft, the server checks both schemas against the comparison fingerprint; changed metadata requires a new comparison.

Drafts explicitly mark **potential data loss** and **manual SQL required**. Existing module definitions, keys/indexes/constraints, special columns, and creation across different mapped schemas require manual work. Dependencies, cyclic references, data conversions, and compatibility with older target servers require review. An incomplete draft is labeled in both the preview and downloaded file; it is not a complete synchronization script. No generated SQL is executed by the app.

SQL Server's column/default rules are documented in [ALTER TABLE column constraints](https://learn.microsoft.com/en-us/sql/t-sql/statements/alter-table-column-constraint-transact-sql?view=sql-server-ver17).

```powershell
dotnet build Workbench.slnx -c Release
node --check wwwroot/js/live.js
dotnet run --project tests/Integration/Integration.csproj -c Release -- --migration-only
# Creates and cleans up only uniquely named Workbench_Test_* databases and login.
# Default admin connection is localhost with Windows integrated authentication.
# Override with WORKBENCH_TEST_ADMIN when needed.
dotnet run --project tests/Integration/Integration.csproj -c Release
# Cookie/key restart checks only; no SQL Server connection or test databases.
dotnet run --project tests/Integration/Integration.csproj -c Release -- --protection-only
```

The integration harness checks live SQL metadata, comparisons, query rejection,
precision, generated schema/INSERT execution in a disposable output database,
EF profile persistence, session isolation, CSRF, and disconnect behavior.

Browser checks (local app running):

```powershell
playwright-cli -s=workbench open http://localhost:5180 --browser=msedge
playwright-cli -s=workbench --raw run-code --filename=tests/ui-smoke.js
playwright-cli -s=workbench --raw run-code --filename=tests/migration-smoke.js
playwright-cli -s=workbench --raw run-code --filename=tests/data-export-smoke.js
./tests/BrowserFixture.ps1 setup
try {
    playwright-cli -s=workbench --raw run-code --filename=tests/.live-smoke.js
} finally {
    ./tests/BrowserFixture.ps1 cleanup
}
```

The live browser script tests actual SQL connections, all tool workflows, .sql
file downloads, rejected writes, mobile layout, and connection removal. Its
credential file is generated locally, excluded from Git, and removed on cleanup.

## Code map

- Controllers/DatabaseController.cs: session-bound API endpoints.
- Services/ConnectionVault.cs: in-memory protected credentials and input policy.
- Services/SqlDatabaseService.cs: metadata and bounded SELECT reads.
- Services/ComparisonService.cs: schema/data matching and differences.
- Services/ScriptService.cs: SMO schema export and INSERT generation.
- Services/SelectValidator.cs: T-SQL AST validation.
- Data/WorkbenchDbContext.cs: optional EF Core profile store.
- Views/Home/Index.cshtml: MVC workspace shell.
- wwwroot/js/live.js: live UI workflows; site.js/demo-data.js: isolated demo mode.
