# API Management configuration

Everything APIM needs that is not infrastructure. The service, its backend, its
named values and its products are created by `infra/modules/apim.bicep`; the API
itself and every policy are applied from here by `scripts/Import-ApimApi.ps1`.

```
openapi/booking-api.yaml        the contract imported into the gateway
policies/api-policy.xml         API scope: auth, rate limits, headers, error shape
policies/operations/*.xml       operation scope, one file per operationId
products/products.json          product metadata and which policy belongs to which
products/*-policy.xml           product scope: the per-tier limits
```

## Why policies are files, not template strings

XML embedded in a Bicep template is unreadable, un-lintable, and impossible to
review in a diff. As files they can be validated before they are applied - which
`Import-ApimApi.ps1` does, refusing to upload anything that is not well-formed
XML, and which CI repeats on every push.

The cost is that applying them is a second step after deployment. That is a fair
trade for being able to review a policy change like any other code change.

## Naming rule that is easy to get wrong

**An operation policy file must be named after its `operationId`.** APIM derives
operation ids from the OpenAPI document, so `getBooking.xml` applies to
`operationId: getBooking`. A file named `get-booking.xml` matches nothing, the
import script logs a warning, and the policy silently does not exist - which for
a caching policy means no caching and no error.

CI asserts the correspondence, because this exact mistake was made while building
this repository.

## Named values

The policy XML references `{{entra-tenant-id}}`, `{{entra-api-audience}}` and
`{{backend-key}}`. All three are created by `infra/modules/apim.bicep`, so the
policy files are environment-agnostic and contain no identifiers at all.

`backend-key` is Key Vault-backed with **no version pinned**, so rotating the
secret in Key Vault propagates to the gateway within four hours without a
redeployment.

## Tier limitations that matter here

| Feature | Consumption | Developer+ |
|---|---|---|
| Built-in cache | **No** - `caching-type="internal"` is silently a no-op | Yes |
| Developer portal | **No** | Yes |
| VNet integration | No | Developer/Premium |

The caching policy uses `prefer-external`, so it works with an attached Azure
Cache for Redis and does nothing harmful without one. See `docs/cost.md`.
