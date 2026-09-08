# Cost

Every default in `infra/main.bicep` is the cheapest SKU that still demonstrates
the pattern. This is what that costs, what each upgrade buys, and how to stop
paying for it.

> **Prices are indicative** — West Europe, USD, retail pay-as-you-go, as a rough
> order of magnitude for planning. Azure pricing changes and varies by region and
> agreement. Check the [pricing calculator](https://azure.microsoft.com/pricing/calculator/)
> before committing to anything, and treat the ratios here as the durable part.

## The default deployment

| Resource | SKU | Rough monthly cost | Notes |
|---|---|---|---|
| API Management | Consumption | ~$0 idle | ~$3.50 per million calls; first million free each month |
| App Service plan | B1 Linux | ~$13 | The only fixed cost worth caring about |
| Service Bus | Basic | ~$0 | $0.05 per million operations; a demo will not register |
| Key Vault | Standard | ~$0 | $0.03 per 10,000 operations |
| Managed identities | — | Free | |
| Log Analytics + App Insights | opt-in, off by default | ~$0 | 5 GB/month free; capped at 1 GB/day |
| **Total** | | **~$13/month** | Dominated entirely by the App Service plan |

## What each upgrade buys

| Change | Extra cost | What you get | Worth it when |
|---|---|---|---|
| App Service **F1** instead of B1 | −$13 (free) | Nothing extra | Never, for this app. F1 has no Always On, so the outbox publisher is unloaded when the app idles and bookings sit `PENDING` until the next request wakes it. Correct, but it looks broken in a demo |
| APIM **Developer** | ~$50/month | Developer portal, built-in cache, VNet integration | You need to show the portal or the cache working. Not for production - no SLA |
| APIM **Basic** | ~$150/month | The above plus a 99.95% SLA | Production |
| Service Bus **Standard** | ~$10/month | Duplicate detection, topics, sessions | You want broker-level deduplication. The worker's ledger already covers it |
| Azure Cache for Redis **C0** | ~$16/month | The `cache-lookup`/`cache-store` policy actually caches on Consumption | Demonstrating gateway caching without paying for APIM Developer |
| Monitoring on | ~$0-3/month | Application Insights traces, live metrics, KQL | Almost always. It is the only component billed by volume, hence opt-in |

## The specific trap

**APIM Developer costs about $50/month whether or not a single request arrives.**
It is the tier people select while following a tutorial, and it is the line item
that shows up on a personal subscription six months later. Consumption is the
right default for a portfolio deployment; switch to Developer only for as long as
you need the portal.

## Keeping the bill at zero between demos

Delete the resource group. Everything here is in the template, so redeploying is
one command:

```powershell
Remove-AzResourceGroup -Name rg-bookingdemo-dev -Force
./scripts/Deploy-Infrastructure.ps1 -ParameterFile ../infra/main.parameters.json
```

Two things to know before you do:

- **Key Vault soft-delete keeps the name reserved for 7 days.** The template sets
  `softDeleteRetentionInDays: 7` and leaves purge protection off for exactly this
  reason. If a redeploy fails with "vault name already in use", purge it:
  `Remove-AzKeyVault -VaultName kv-bookingdemo-a1b2c3 -InRemovedState -Location westeurope -Force`
- **Service Bus namespace names are globally unique**, and the template derives
  the suffix from the subscription id, so a redeploy into the same subscription
  reuses the same name. That is intentional.

To keep the resources but stop the largest charge, stop the App Service - the plan
is still billed, so scaling the plan to F1 is the cheaper pause:

```powershell
Stop-AzWebApp -ResourceGroupName rg-bookingdemo-dev -Name app-bookingdemo-api-dev-a1b2c3
```

## Cost management, once

Worth five minutes on any personal subscription:

```powershell
New-AzConsumptionBudget -Name 'portfolio-budget' -Amount 25 -Category Cost `
    -TimeGrain Monthly -StartDate (Get-Date -Day 1).Date `
    -ContactEmail 'you@example.com' -NotificationKey 'eightyPercent' `
    -NotificationThreshold 80 -NotificationEnabled
```

A budget does not stop spending; it tells you it is happening. That is still the
difference between a $13 surprise and a $300 one.
