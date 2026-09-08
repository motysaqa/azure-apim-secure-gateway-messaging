#Requires -Version 7.0
#Requires -Modules Az.Accounts, Az.ApiManagement

<#
.SYNOPSIS
    Imports the Booking API into API Management and applies every policy.

.DESCRIPTION
    Imports apim/openapi/booking-api.yaml, then applies:

      * the API-scoped policy      apim/policies/api-policy.xml
      * operation policies         apim/policies/operations/*.xml
      * product policies           apim/products/*-policy.xml
      * product/API associations   apim/products/products.json

    Policies live as files rather than inside the Bicep template on purpose. XML
    embedded in a template is unreadable, un-lintable and impossible to review in
    a diff; as files they can be validated before they are applied - which this
    script does, refusing to upload anything that is not well-formed XML.

    Imports go to a **revision** by default, not over the live API. A revision can
    be inspected and tested before it is made current, so a bad policy does not
    take the gateway down.

.PARAMETER ResourceGroupName
    Resource group containing the API Management service.

.PARAMETER ApimName
    API Management service name.

.PARAMETER ApiId
    API identifier inside APIM. Must match the backend-id used in the policies.

.PARAMETER ApiPath
    Public path suffix. The gateway URL becomes https://<apim>.azure-api.net/<ApiPath>.

.PARAMETER MakeCurrent
    Promote the imported revision to current. Without this the revision is created
    but left for inspection.

.EXAMPLE
    ./Import-ApimApi.ps1 -ResourceGroupName rg-booking-dev -ApimName apim-booking-dev-a1b2c3

.EXAMPLE
    ./Import-ApimApi.ps1 -ResourceGroupName rg-booking-dev -ApimName apim-booking-dev-a1b2c3 -MakeCurrent
#>

[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)] [string] $ResourceGroupName,
    [Parameter(Mandatory)] [string] $ApimName,

    [string] $ApiId = 'booking-api',
    [string] $ApiPath = 'booking/v1',
    [switch] $MakeCurrent
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot     = Split-Path -Parent $PSScriptRoot
$specPath     = Join-Path $repoRoot 'apim/openapi/booking-api.yaml'
$apiPolicy    = Join-Path $repoRoot 'apim/policies/api-policy.xml'
$operationDir = Join-Path $repoRoot 'apim/policies/operations'
$productDir   = Join-Path $repoRoot 'apim/products'

function Write-Step {
    param([string] $Message)
    Write-Host ''
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Assert-WellFormedXml {
    <#
        A malformed policy is rejected by the service with a message that points at
        a character offset in a flattened document, which is close to useless.
        Failing here instead means the error names the file.
    #>
    param([Parameter(Mandatory)][string] $Path)

    try {
        [xml](Get-Content -Path $Path -Raw) | Out-Null
    }
    catch {
        throw "$Path is not well-formed XML: $($_.Exception.Message)"
    }
}

# --- Preconditions ---------------------------------------------------------

Write-Step 'Validating local files'

foreach ($required in $specPath, $apiPolicy) {
    if (-not (Test-Path $required)) { throw "Missing required file: $required" }
}

Assert-WellFormedXml -Path $apiPolicy
Write-Host "OK  $(Split-Path $apiPolicy -Leaf)"

$operationPolicies = @()
if (Test-Path $operationDir) {
    $operationPolicies = Get-ChildItem -Path $operationDir -Filter '*.xml' -File
    foreach ($file in $operationPolicies) {
        Assert-WellFormedXml -Path $file.FullName
        Write-Host "OK  operations/$($file.Name)"
    }
}

$productPolicies = Get-ChildItem -Path $productDir -Filter '*-policy.xml' -File -ErrorAction SilentlyContinue
foreach ($file in $productPolicies) {
    Assert-WellFormedXml -Path $file.FullName
    Write-Host "OK  products/$($file.Name)"
}

if (-not (Get-AzContext)) { throw 'Not signed in. Run Connect-AzAccount first.' }

$apimContext = New-AzApiManagementContext -ResourceGroupName $ResourceGroupName -ServiceName $ApimName

# --- Import the specification ---------------------------------------------

Write-Step "Importing $specPath"

$existing = Get-AzApiManagementApi -Context $apimContext -ApiId $ApiId -ErrorAction SilentlyContinue

$importArgs = @{
    Context          = $apimContext
    SpecificationFormat = 'OpenApi'
    SpecificationPath   = $specPath
    Path                = $ApiPath
    ApiId               = $ApiId
}

if ($existing) {
    # Non-breaking changes go to a new revision, so the live API keeps serving
    # while the change is reviewed. A breaking change would be a new *version*
    # instead - see docs/apim-request-flow.md.
    $revisions = Get-AzApiManagementApiRevision -Context $apimContext -ApiId $ApiId
    $nextRevision = ([int]($revisions | ForEach-Object { [int]$_.ApiRevision } | Measure-Object -Maximum).Maximum) + 1

    Write-Host "The API exists; importing as revision $nextRevision"
    $importArgs.ApiRevision = "$nextRevision"
}
else {
    Write-Host 'Creating the API for the first time'
}

if ($PSCmdlet.ShouldProcess($ApiId, 'Import OpenAPI specification')) {
    $api = Import-AzApiManagementApi @importArgs
    Write-Host "Imported $($api.Name) at /$ApiPath" -ForegroundColor Green
}

# --- Policies --------------------------------------------------------------

Write-Step 'Applying the API policy'

if ($PSCmdlet.ShouldProcess($ApiId, 'Set API policy')) {
    Set-AzApiManagementPolicy -Context $apimContext `
                              -ApiId $ApiId `
                              -PolicyFilePath $apiPolicy `
                              -Format 'application/vnd.ms-azure-apim.policy.raw+xml'
    Write-Host 'API policy applied.' -ForegroundColor Green
}

if ($operationPolicies) {
    Write-Step 'Applying operation policies'

    foreach ($file in $operationPolicies) {
        # File name is the operationId, which is what the OpenAPI import uses as
        # the APIM operation id.
        $operationId = [System.IO.Path]::GetFileNameWithoutExtension($file.Name)

        $operation = Get-AzApiManagementOperation -Context $apimContext `
                                                  -ApiId $ApiId `
                                                  -OperationId $operationId `
                                                  -ErrorAction SilentlyContinue
        if (-not $operation) {
            Write-Warning "No operation '$operationId' on $ApiId; skipping $($file.Name). Check that the file name matches an operationId in the OpenAPI document."
            continue
        }

        if ($PSCmdlet.ShouldProcess("$ApiId/$operationId", 'Set operation policy')) {
            Set-AzApiManagementPolicy -Context $apimContext `
                                      -ApiId $ApiId `
                                      -OperationId $operationId `
                                      -PolicyFilePath $file.FullName `
                                      -Format 'application/vnd.ms-azure-apim.policy.raw+xml'
            Write-Host "  $operationId" -ForegroundColor Green
        }
    }
}

# --- Products --------------------------------------------------------------

Write-Step 'Wiring up products'

$productsFile = Join-Path $productDir 'products.json'
if (-not (Test-Path $productsFile)) {
    Write-Warning "No products.json at $productsFile; skipping product configuration."
}
else {
    $products = (Get-Content $productsFile -Raw | ConvertFrom-Json).products

    foreach ($product in $products) {
        $existingProduct = Get-AzApiManagementProduct -Context $apimContext `
                                                      -ProductId $product.name `
                                                      -ErrorAction SilentlyContinue
        if (-not $existingProduct) {
            Write-Warning "Product '$($product.name)' does not exist. It is created by infra/main.bicep - deploy the infrastructure first."
            continue
        }

        if ($PSCmdlet.ShouldProcess($product.name, "Add API $ApiId")) {
            Add-AzApiManagementApiToProduct -Context $apimContext `
                                            -ProductId $product.name `
                                            -ApiId $ApiId `
                                            -ErrorAction SilentlyContinue
        }

        $policyPath = Join-Path $productDir $product.policyFile
        if ((Test-Path $policyPath) -and $PSCmdlet.ShouldProcess($product.name, 'Set product policy')) {
            Set-AzApiManagementPolicy -Context $apimContext `
                                      -ProductId $product.name `
                                      -PolicyFilePath $policyPath `
                                      -Format 'application/vnd.ms-azure-apim.policy.raw+xml'
            Write-Host "  $($product.name): API added, policy applied" -ForegroundColor Green
        }
    }
}

# --- Promote ---------------------------------------------------------------

if ($MakeCurrent -and $importArgs.ContainsKey('ApiRevision')) {
    Write-Step "Promoting revision $($importArgs.ApiRevision) to current"

    if ($PSCmdlet.ShouldProcess($ApiId, "Make revision $($importArgs.ApiRevision) current")) {
        New-AzApiManagementApiRelease -Context $apimContext `
                                      -ApiId $ApiId `
                                      -ApiRevision $importArgs.ApiRevision `
                                      -Note "Released by Import-ApimApi.ps1 on $(Get-Date -Format 'u')" | Out-Null
        Write-Host 'Revision is now current.' -ForegroundColor Green
    }
}
elseif ($importArgs.ContainsKey('ApiRevision')) {
    Write-Host ''
    Write-Warning "Revision $($importArgs.ApiRevision) was created but is not current. Test it, then re-run with -MakeCurrent."
}

Write-Step 'Done'

Write-Host @"
Verify the gateway before handing a key to anyone:

  ./Invoke-SmokeTests.ps1 -GatewayUrl https://$ApimName.azure-api.net/$ApiPath -SubscriptionKey <key>

A subscription key comes from a subscription against one of the products:

  New-AzApiManagementSubscription -Context (New-AzApiManagementContext -ResourceGroupName $ResourceGroupName -ServiceName $ApimName) ``
      -ProductId booking-starter -Name 'smoke-tests'
"@
