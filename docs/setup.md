# Setup

Everything that can be automated is. What is left needs a browser, an Azure
subscription, or a directory permission a subscription deployment does not have.

## Run it locally first

No Azure account required. The API falls back to an in-memory publisher when no
Service Bus namespace is configured, so the outbox, idempotency and validation
paths all work from a clone.

```bash
dotnet test BookingPlatform.sln                    # 29 tests, no Azure needed

dotnet run --project src/Booking.Api                # http://localhost:5080

curl -s -X POST http://localhost:5080/bookings \
  -H 'Content-Type: application/json' \
  -H "Idempotency-Key: $(uuidgen)" \
  -d '{"propertyId":"PROP-1001","guestEmail":"guest@example.com",
       "checkIn":"2026-11-01","checkOut":"2026-11-05","guests":2,
       "totalAmount":592.40,"currency":"EUR"}' | jq

curl -s http://localhost:5080/health/ready | jq     # outbox depth
```

Send the same `Idempotency-Key` twice and the second call returns `200` with
`Idempotency-Replayed: true` and the original booking.

## What you must do manually

### 1. Prerequisites

| Tool | Version | Needed for |
|---|---|---|
| .NET SDK | 8.0+ | Build, test, publish |
| PowerShell | 7.0+ | The deployment scripts |
| Az PowerShell module | Current | `Install-Module Az -Scope CurrentUser` |
| Azure CLI | Current | Optional, for `what-if` previews |
| Python | 3.10+ | `scripts/reconcile_bookings.py` |

```powershell
Install-Module Az -Scope CurrentUser -Repository PSGallery -Force
Connect-AzAccount
python -m pip install -r scripts/requirements.txt
```

### 2. Register the Entra ID applications

[entra-id-setup.md](entra-id-setup.md), in full. Two registrations, application
roles with member type **Applications**, and admin consent granted. Note the
tenant id, the API's application id, and the partner client's id and secret.

Do this before deploying: the tenant id and API application id are Bicep
parameters, and without them the `validate-jwt` policy has no issuer to check
against.

### 3. Prepare the parameters

```powershell
Copy-Item infra/main.parameters.example.json infra/main.parameters.json
```

Edit it. At minimum set `namePrefix`, `apimPublisherEmail`, `entraTenantId` and
`entraApiApplicationId`. `main.parameters.json` is gitignored, and CI fails if one
is ever committed.

There is deliberately no `backendKey` parameter in that file:
`Deploy-Infrastructure.ps1` generates it from a CSPRNG and passes it as a secure
parameter, so the only copy lives in Key Vault.

### 4. Deploy

```powershell
./scripts/Deploy-Infrastructure.ps1 -ParameterFile ../infra/main.parameters.json -WhatIf   # preview
./scripts/Deploy-Infrastructure.ps1 -ParameterFile ../infra/main.parameters.json
```

The script provisions the infrastructure, publishes the API, waits for it to
report healthy, and restricts the App Service to API Management's outbound
addresses. That last step is separate because APIM needs the app's hostname, so
the app must exist first - a genuine ordering constraint.

Expect 30-45 minutes on a first run. APIM Consumption is the slow part.

### 5. Import the API and apply the policies

```powershell
./scripts/Import-ApimApi.ps1 -ResourceGroupName rg-<prefix>-dev -ApimName apim-<prefix>-dev-<suffix>
```

The first run creates the API. Later runs import to a **revision** and leave it
for inspection; add `-MakeCurrent` to promote it.

### 6. Create a subscription key

```powershell
$context = New-AzApiManagementContext -ResourceGroupName rg-<prefix>-dev -ServiceName apim-<prefix>-dev-<suffix>
New-AzApiManagementSubscription -Context $context -ProductId booking-starter -Name 'smoke-tests'
Get-AzApiManagementSubscriptionKey -Context $context -SubscriptionId <id>
```

### 7. Smoke-test through the gateway

```powershell
./scripts/Invoke-SmokeTests.ps1 `
    -GatewayUrl https://apim-<prefix>-dev-<suffix>.azure-api.net/booking/v1 `
    -TenantId <tenantId> -ClientId <partnerClientId> -Scope "api://<apiAppId>/.default"
```

Every credential is a `SecureString` and is prompted for if not supplied, so
nothing lands in shell history.

### 8. Publish the developer portal

Only on Developer tier or above - Consumption has no portal. See
[apim-request-flow.md](apim-request-flow.md#developer-portal).

---

## Where the worker runs

**`infra/main.bicep` does not provision compute for `Booking.Worker`.** That is a
decision, not an omission, and it is worth understanding before you deploy.

`Booking.Worker` is a console host with no HTTP listener. Linux App Service
expects a process listening on `$PORT` and restarts anything that does not, so
deploying it as a web app produces a restart loop that looks like a crash. The
three honest options:

| Option | Cost | Trade-off |
|---|---|---|
| **Azure Container Apps** (recommended) | Scales to zero; free grant covers a demo | Needs a container image and a registry |
| **App Service WebJob** (continuous) | Shares the existing B1 plan, so free | WebJobs cannot be deployed from Bicep; it is a separate zip push. Needs Always On, so not on F1 |
| **Give the worker a health endpoint** | Free on the existing plan | Change the Worker SDK to Web, add `/health`, deploy as a second app. Simple, and slightly dishonest about what a worker is |

What the template *does* do is create the user-assigned managed identity with
**Azure Service Bus Data Receiver** scoped to the queue, so whichever host you
choose gets an identity that already has exactly the rights it needs:

```powershell
$identity = (Get-AzUserAssignedIdentity -ResourceGroupName rg-<prefix>-dev -Name id-<prefix>-worker-dev)
$identity.ClientId       # AZURE_CLIENT_ID for DefaultAzureCredential
$identity.Id             # the resource id to attach to the host
```

Run the worker locally against the real queue in the meantime - `az login` is
enough, because `DefaultAzureCredential` falls back to your developer identity:

```powershell
$env:ServiceBus__FullyQualifiedNamespace = 'sb-<prefix>-dev-<suffix>.servicebus.windows.net'
dotnet run --project src/Booking.Worker
```

You will need **Azure Service Bus Data Receiver** on the queue for your own user
account to do that; the role assignment in the template is for the worker's
identity, not for you.

---

## Reconciliation

Once both sides are running, check they agree:

```bash
python scripts/reconcile_bookings.py \
  --api-url https://apim-<prefix>-dev-<suffix>.azure-api.net/booking/v1 \
  --subscription-key "$KEY" \
  --worker-db ./worker.db
```

Exit code 0 means the two stores agree; 1 means they do not, and the report says
which bookings and which fields. Worth running on a schedule in anything real -
asynchronous systems drift quietly, and nothing raises an error when they do.

## Tearing it down

```powershell
Remove-AzResourceGroup -Name rg-<prefix>-dev -Force
```

Key Vault soft-delete reserves the name for 7 days; [cost.md](cost.md) has the
purge command if you want to redeploy sooner.
