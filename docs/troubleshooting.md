# Troubleshooting

Split deliberately: problems that actually occurred while building this
repository, with the real error text, kept separate from failure modes the code
guards against but that were not reproduced here.

---

## Part 1 — Hit while building this repository

### `SQLite Error 14: 'unable to open database file'`

**Where:** starting `Booking.Api` with the database pointed at a directory that
did not exist.

```
Unhandled exception. Microsoft.Data.Sqlite.SqliteException (0x80004005):
SQLite Error 14: 'unable to open database file'.
   at Booking.Api.Storage.BookingStore..ctor(...)
```

**Cause:** SQLite does not create the parent directory, and the error names
neither the path nor the reason.

**Why it mattered beyond local development:** the App Service setting in
`infra/modules/appService.bicep` is
`ConnectionStrings__Bookings = Data Source=/home/data/bookings.db`, and
`/home/data` does not exist on a fresh App Service. The same crash was waiting in
Azure, with the added inconvenience of only being visible in the log stream.

**Fix:** both stores now create the directory before opening the connection.

```csharp
private static void EnsureDirectoryExists(string connectionString)
{
    var dataSource = new SqliteConnectionStringBuilder(connectionString).DataSource;
    if (string.IsNullOrWhiteSpace(dataSource) || dataSource.StartsWith(":memory:", StringComparison.Ordinal))
        return;

    var directory = Path.GetDirectoryName(Path.GetFullPath(dataSource));
    if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        Directory.CreateDirectory(directory);
}
```

### An APIM policy that is not well-formed XML

**Where:** writing `apim/policies/api-policy.xml`.

Two of them, both silent until the policy is uploaded and rejected with a
character offset into a flattened document:

1. **A raw `<` inside an attribute.**
   `increment-condition="@(context.Response.StatusCode < 500)"` is not valid XML.
   It has to be `&lt;`.
2. **Nested double quotes inside an attribute.**
   `template="@(context.Request.OriginalUrl.Path.Replace("/booking/v1", ""))"`
   terminates the attribute at the second quote. C# string literals need
   `&quot;`, which is what `operations/getReservation.xml` uses.

**Fix:** `Import-ApimApi.ps1` parses every policy file locally before uploading
anything, so the error names the file. CI does the same on every push.

### The CI hygiene checks caught two real defects

Both were found by checks written for this repository, on their first run:

1. **Operation policy files were named `get-booking.xml`**, while APIM derives
   operation ids from the OpenAPI document, where it is `getBooking`. The script
   would have logged a warning and applied no policy at all - a caching policy
   that silently does nothing is worse than no policy. Files renamed to match the
   `operationId` exactly, and CI now asserts the correspondence.
2. **The first GUID check was too blunt.** It flagged Azure's built-in role
   definition ids, which are the same in every tenant and are meant to be
   literal. Rewritten with an allowlist of those three ids, so a real tenant or
   client id committed by accident is still caught.

### Verified by running it

- `dotnet test`: **29 tests pass** - 19 API integration tests through
  `WebApplicationFactory` against the real endpoints, and 10 worker handler tests.
- `bicep build infra/main.bicep`: compiles with **no warnings**. It did warn
  once - `BCP318`, a possibly-null module output from the conditional monitoring
  module - fixed with the `!` null-forgiving operator.
- The API was run locally, three bookings created, the outbox observed draining
  to zero and the bookings moving to `CONFIRMED`.
- `reconcile_bookings.py` was run against a seeded worker store in both
  directions: it correctly reported a missing confirmation, an orphaned booking, a
  `totalAmount` mismatch and a dead letter (exit 1), and correctly reported no
  discrepancies once the two sides agreed (exit 0). It also correctly did *not*
  report `592.4` versus `592.40` as a mismatch, which is the false positive that
  makes people ignore a reconciliation report.

### Not verified

Nothing in this repository has been deployed to Azure. The Bicep compiles and the
scripts parse, but no `New-AzSubscriptionDeployment` has been run, no APIM
instance exists, and no policy has been applied to a live gateway. Expect to fix
something on the first real deployment - Azure resource providers have opinions
that only surface at deployment time.

---

## Part 2 — Guarded against, not reproduced here

### Bookings stay `PENDING` forever

The outbox is written but never drained. In order of likelihood:

| Cause | How to confirm | Fix |
|---|---|---|
| App Service on **F1** | `alwaysOnEnabled` output is `false` | No Always On, so the publisher is unloaded when the app idles. Move to B1 |
| Managed identity has no send rights | `az role assignment list --assignee <principalId>` shows nothing on the queue | Redeploy; the role assignment is in `appService.bicep` |
| Role assignment not propagated yet | It works ten minutes later | Azure RBAC takes up to 5 minutes to propagate. It is not a code problem |
| Wrong namespace | `/health/ready` reports `destination: in-memory` | `ServiceBus__FullyQualifiedNamespace` is empty; the app fell back to the in-memory publisher |

`GET /health/ready` reports the outbox depth for exactly this reason. A `pending`
count that does not fall is the signal, and it is the right thing to alert on.

### `Unauthorized` from Service Bus with a managed identity

`DefaultAzureCredential` resolves to different identities in different places:
the App Service system-assigned identity in Azure, your `az login` locally, and
sometimes a stale Visual Studio account. When it picks the wrong one the error
says only that the token is invalid.

Set `AZURE_CLIENT_ID` explicitly when a user-assigned identity is in play, and
check which identity was actually used by enabling
`Azure.Identity` diagnostics.

### Messages arrive but nothing is stored

Check the dead-letter sub-queue before the code:

```powershell
Get-AzServiceBusQueue -ResourceGroupName rg-x -NamespaceName sb-x -Name booking-created |
    Select-Object -ExpandProperty CountDetails
```

A non-zero `DeadLetterMessageCount` means the handler decided the messages will
never succeed. The reason and description are on each message, and the worker also
records them in its own `dead_letters` table so they can be read without peeking
the queue.

### A duplicate booking appears

Work through the three layers in order:

1. Did the client send an `Idempotency-Key`? Without one, two `POST`s are two
   bookings, and correctly so.
2. Is the worker's `processed_messages` ledger intact? A rebuilt worker database
   loses the ledger, and every redelivered message is then new.
3. Are two workers writing to *different* databases? SQLite is per-instance. This
   is the reason the README lists SQL Server as the first thing to replace: with
   two instances and two files, the ledger is not shared and deduplication is
   local to whichever instance happened to receive the message.

### The gateway returns 401 for a token that looks correct

Almost always one of the four claims in
[entra-id-setup.md](entra-id-setup.md#5-verify-the-token-before-touching-the-gateway).
Paste the token into <https://jwt.ms> before suspecting the policy.

The one that is not a claim problem: the `openid-config` URL must use the `/v2.0`
issuer to match a v2 token. A v1 issuer with a v2 token produces
`IDX10205: Issuer validation failed` with two strings that look identical apart
from the suffix.

### Environment issues that look like code issues

| Symptom | Actual cause |
|---|---|
| `The subscription is not registered to use namespace 'Microsoft.ApiManagement'` | `Register-AzResourceProvider -ProviderNamespace Microsoft.ApiManagement` |
| Key Vault deploy fails with "name already in use" | Soft-delete. Purge it - see [cost.md](cost.md) |
| `dotnet publish` output is empty on the App Service | The zip was built from the wrong directory; the deploy script archives `publish/*`, not `publish` |
| APIM takes 30+ minutes | Normal. Consumption is faster than Developer but still not fast |
| The App Service returns 403 to everything after deployment | The IP restriction is working. Traffic must go through the gateway; use `-SkipIpRestriction` while debugging, and remember to undo it |
