# Entra ID app registration

App registrations cannot be created from this repository's templates - they live
in the directory, not the subscription, and creating them needs directory
permissions that a subscription deployment does not have. These are the manual
steps, in order.

Two registrations are needed: one representing **the API** (what a token is
issued *for*) and one representing **the partner** (who requests the token). Using
a single registration for both is the most common shortcut here, and it makes the
audience check meaningless.

---

## 1. Register the API

1. **Entra ID → App registrations → New registration**
   - Name: `Booking API`
   - Supported account types: **Accounts in this organizational directory only**
   - Redirect URI: leave empty - this is a daemon-facing API, not a web app
2. Note the **Application (client) ID** and **Directory (tenant) ID**. These become
   the `entraApiApplicationId` and `entraTenantId` Bicep parameters.

## 2. Expose it as a resource

1. **Manage → Expose an API → Application ID URI → Set**
   - Accept the default `api://{applicationId}`. This exact string is the audience
     the `validate-jwt` policy checks, so whatever you set here must match the
     `entra-api-audience` named value.

## 3. Define application roles

Partner systems call without a signed-in user, so they need **application
permissions** (`roles` in the token), not delegated scopes.

1. **Manage → App roles → Create app role**

   | Field | Value |
   |---|---|
   | Display name | `Read bookings` |
   | Allowed member types | **Applications** |
   | Value | `Booking.Read` |
   | Description | Read booking details |

2. Repeat for `Booking.Write` (*Create bookings*).

`Allowed member types` must be **Applications**. Choosing *Users/Groups* produces
a role that never appears in a client-credentials token, and the `validate-jwt`
policy then rejects every request with no obvious reason why.

## 4. Register the partner client

1. **App registrations → New registration**
   - Name: `Booking Channel Partner (test)`
   - Supported account types: single tenant
2. **Certificates & secrets → New client secret**. Copy the value immediately - it
   is shown once. Keep it out of this repository; the smoke-test script takes it
   as a `SecureString` and prompts for it if omitted.
3. **API permissions → Add a permission → My APIs → Booking API →
   Application permissions** → tick `Booking.Read` and `Booking.Write` → Add.
4. **Grant admin consent** for the directory. Without this the permissions are
   requested but not granted, and the issued token simply has no `roles` claim.
   This is the single most common cause of an unexplained 401 in this setup.

> A client secret is fine for a portfolio project. For anything real, use a
> certificate or workload identity federation - a secret in a partner's
> configuration is a secret that eventually appears in a support ticket.

## 5. Verify the token before touching the gateway

```bash
curl -s -X POST "https://login.microsoftonline.com/$TENANT_ID/oauth2/v2.0/token" \
  -d "client_id=$CLIENT_ID" \
  -d "client_secret=$CLIENT_SECRET" \
  -d "scope=api://$API_APP_ID/.default" \
  -d "grant_type=client_credentials" | jq -r .access_token
```

Paste the result into <https://jwt.ms> and confirm all four:

| Claim | Expected | If it is wrong |
|---|---|---|
| `aud` | `api://{apiApplicationId}` | The `scope` was wrong; it must be the API's URI plus `/.default` |
| `iss` | `https://login.microsoftonline.com/{tenantId}/v2.0` | A v1.0 issuer means the app registration's `accessTokenAcceptedVersion` is not 2 |
| `roles` | `["Booking.Read","Booking.Write"]` | Admin consent was not granted, or the role's member type is not Applications |
| `appid` / `azp` | The partner client's id | You used the API's own credentials |

Getting this right before configuring the gateway saves a great deal of time: a
policy that rejects a token is indistinguishable from a policy that is
misconfigured, and only one of the two is your fault.

## 6. Wire the values into the deployment

```json
{
  "entraTenantId":        { "value": "<Directory (tenant) ID>" },
  "entraApiApplicationId": { "value": "<Booking API Application (client) ID>" }
}
```

Redeploy. `infra/modules/apim.bicep` writes them into the `entra-tenant-id` and
`entra-api-audience` named values, which the committed policy XML references as
`{{...}}`. The policy file itself stays environment-agnostic and contains no
identifiers - which is also what the CI secret-hygiene job enforces.

## 7. Confirm end to end

```powershell
./scripts/Invoke-SmokeTests.ps1 `
    -GatewayUrl https://<apim>.azure-api.net/booking/v1 `
    -TenantId <tenantId> -ClientId <partnerClientId> -Scope "api://<apiAppId>/.default"
```

## Troubleshooting

| Symptom | Cause |
|---|---|
| 401, `IDX10214: Audience validation failed` | The `scope` used to request the token does not match the Application ID URI |
| 401, no detail | Token acquired against the wrong tenant, or `roles` missing because consent was not granted |
| 401 only after some hours | The signing key rolled over and the JWKS cache is stale. `openid-config` handles rollover automatically - a pinned key does not, which is why the policy uses the former |
| `AADSTS7000215: Invalid client secret` | The secret's **value** was not copied; the portal also shows a secret **ID**, which is not it |
| `AADSTS500011` | The `scope` names a resource that does not exist in the tenant - usually a typo in the application ID URI |
