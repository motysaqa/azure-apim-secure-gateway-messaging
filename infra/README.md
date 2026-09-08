# Infrastructure

Bicep templates for the whole stack, deployed at subscription scope so the
resource group is part of the template rather than a manual prerequisite.

```
main.bicep                    subscription scope: resource group + every module
main.parameters.example.json  copy to main.parameters.json (gitignored) and edit
modules/
  serviceBus.bicep            namespace + booking-created queue, SAS auth disabled
  keyVault.bicep              RBAC vault holding the single backend key
  appService.bicep            Linux plan + API app, identity and role assignments
  apim.bicep                  gateway, Key Vault-backed named value, backend, products
  monitoring.bicep            Log Analytics + Application Insights (opt-in)
  workerIdentity.bicep        user-assigned identity with receive rights on the queue
```

## Deploy

```powershell
Copy-Item main.parameters.example.json main.parameters.json   # then edit it
../scripts/Deploy-Infrastructure.ps1 -ParameterFile ./main.parameters.json
```

`backendKey` is intentionally absent from the parameters file. The deploy script
generates a random value and passes it in, so the only copy ends up in Key Vault.

## Preview before deploying

```bash
az deployment sub what-if --location westeurope \
  --template-file main.bicep --parameters @main.parameters.json \
  --parameters backendKey=$(openssl rand -base64 32)
```

## Design notes

**No connection strings anywhere.** Service Bus has `disableLocalAuth: true`, so
SAS keys do not exist to be leaked. The API sends with its system-assigned
identity, the worker receives with a user-assigned one, and both role assignments
are scoped to the queue rather than the namespace - a queue added later inherits
nothing.

**One secret, and only because it cannot be an identity.** The backend key is a
value APIM injects into a header, so it has to be a string. It lives in Key Vault,
APIM reads it as a named value with no version pinned (rotation propagates within
four hours), and the App Service reads it through a Key Vault reference. Neither
service ever holds a copy in its own configuration.

**System-assigned for the API, user-assigned for the worker.** The API's identity
should die with the app. The worker is not provisioned by this template - see
`docs/setup.md`, "Where the worker runs" - so its identity is user-assigned and
created up front, ready to be attached to whatever ends up hosting it.

**Monitoring is opt-in.** It is the only component billed by ingestion volume, so
`deployMonitoring` defaults to `false`. Set it to `true` and Log Analytics is
capped at 1 GB/day, comfortably inside the free grant.

## What this template does not do

- **Deploy application code.** `Deploy-Infrastructure.ps1` handles that separately
  with `az webapp deploy`.
- **Import the API into APIM.** That is `scripts/Import-ApimApi.ps1`, because the
  OpenAPI document and the policy XML are files in this repo that would have to be
  inlined into the template otherwise.
- **Restrict the App Service to APIM's outbound IPs.** It cannot: APIM needs the
  app's hostname, so the app must exist first. `Deploy-Infrastructure.ps1` applies
  the restriction once both are up.
- **Provision the worker's compute.** Deliberately - see `docs/setup.md`.
