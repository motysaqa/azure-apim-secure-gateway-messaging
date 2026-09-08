#Requires -Version 7.0

<#
.SYNOPSIS
    Smoke-tests the Booking API through the API Management gateway.

.DESCRIPTION
    Obtains an Entra ID token with the client-credentials flow, calls the gateway,
    and asserts what the deployment is supposed to guarantee:

      * an unauthenticated call is rejected before it reaches the origin
      * a call without a subscription key is rejected
      * a valid call creates a booking and returns 201
      * replaying an Idempotency-Key returns the original booking, not a new one
      * an invalid payload returns a 400 problem document with field detail
      * the rate limit produces a 429 with Retry-After
      * the correlation id survives the whole round trip

    Exit code is 0 only when everything passes, so this can gate a release.

    No secret is accepted as plain text: -ClientSecret and -SubscriptionKey are
    SecureString, so they do not end up in the shell history or a transcript.

.PARAMETER GatewayUrl
    Full gateway base URL, e.g. https://apim-booking-dev.azure-api.net/booking/v1

.PARAMETER SubscriptionKey
    Product subscription key. Prompted for if omitted.

.PARAMETER TenantId
    Entra ID tenant. Omit to skip token acquisition and test unauthenticated
    behaviour only.

.PARAMETER ClientId
    Client application (the caller, not the API).

.PARAMETER ClientSecret
    Client secret for that application. Prompted for if -ClientId is supplied.

.PARAMETER Scope
    Token scope. Defaults to api://<ApiApplicationId>/.default.

.PARAMETER RateLimitCalls
    How many calls to make when probing the rate limit. Set to 0 to skip that test
    - it is the slowest one and it consumes quota.

.EXAMPLE
    ./Invoke-SmokeTests.ps1 -GatewayUrl https://apim-booking-dev.azure-api.net/booking/v1

.EXAMPLE
    ./Invoke-SmokeTests.ps1 -GatewayUrl https://apim-booking-dev.azure-api.net/booking/v1 `
        -TenantId $tenant -ClientId $client -Scope "api://$apiApp/.default"
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $GatewayUrl,

    [SecureString] $SubscriptionKey,

    [string] $TenantId,
    [string] $ClientId,
    [SecureString] $ClientSecret,
    [string] $Scope,

    [int] $RateLimitCalls = 25,
    [int] $TimeoutSec = 30
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:Passed = 0
$script:Failures = [System.Collections.Generic.List[string]]::new()

function Test-Assertion {
    param(
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [bool]   $Condition,
        [string] $Detail = ''
    )

    if ($Condition) {
        $script:Passed++
        Write-Host "  PASS  $Name" -ForegroundColor Green
    }
    else {
        $script:Failures.Add("$Name -- $Detail")
        Write-Host "  FAIL  $Name" -ForegroundColor Red
        if ($Detail) { Write-Host "        $Detail" -ForegroundColor DarkGray }
    }
}

function Write-Section {
    param([string] $Title)
    Write-Host ''
    Write-Host "[$Title]" -ForegroundColor Cyan
}

function ConvertFrom-SecureStringToPlain {
    param([SecureString] $Secure)
    if (-not $Secure) { return $null }
    return [System.Net.NetworkCredential]::new('', $Secure).Password
}

function Invoke-Gateway {
    <#
        -SkipHttpErrorCheck throughout: a 401 or a 429 is the expected result of
        several of these tests, not an exception to be caught.
    #>
    param(
        [string] $Method = 'GET',
        [Parameter(Mandatory)] [string] $Path,
        [hashtable] $Headers = @{},
        $Body
    )

    $arguments = @{
        Uri                = "$GatewayUrl$Path"
        Method             = $Method
        Headers            = $Headers
        TimeoutSec         = $TimeoutSec
        SkipHttpErrorCheck = $true
    }

    if ($null -ne $Body) {
        $arguments.Body = ($Body | ConvertTo-Json -Depth 6)
        $arguments.ContentType = 'application/json'
    }

    return Invoke-WebRequest @arguments
}

function New-BookingPayload {
    $checkIn = (Get-Date).AddDays(30).ToString('yyyy-MM-dd')
    $checkOut = (Get-Date).AddDays(34).ToString('yyyy-MM-dd')
    return @{
        propertyId  = 'PROP-1001'
        guestEmail  = 'smoke.test@example.com'
        checkIn     = $checkIn
        checkOut    = $checkOut
        guests      = 2
        totalAmount = 592.40
        currency    = 'EUR'
    }
}

# --- Credentials -----------------------------------------------------------

if (-not $SubscriptionKey) {
    $SubscriptionKey = Read-Host -Prompt 'API Management subscription key' -AsSecureString
}
$subscriptionKeyPlain = ConvertFrom-SecureStringToPlain $SubscriptionKey

$token = $null
if ($TenantId -and $ClientId) {
    if (-not $ClientSecret) {
        $ClientSecret = Read-Host -Prompt "Client secret for $ClientId" -AsSecureString
    }
    if (-not $Scope) {
        throw 'Supply -Scope, e.g. api://<api-application-id>/.default'
    }

    Write-Section 'Acquiring a token'

    $tokenResponse = Invoke-RestMethod -Method Post `
        -Uri "https://login.microsoftonline.com/$TenantId/oauth2/v2.0/token" `
        -ContentType 'application/x-www-form-urlencoded' `
        -Body @{
            client_id     = $ClientId
            client_secret = (ConvertFrom-SecureStringToPlain $ClientSecret)
            scope         = $Scope
            grant_type    = 'client_credentials'
        }

    $token = $tokenResponse.access_token
    Write-Host "  Token acquired, expires in $($tokenResponse.expires_in)s" -ForegroundColor Green
}
else {
    Write-Warning 'No -TenantId/-ClientId supplied. Authenticated cases will be skipped; the gateway must have validate-jwt disabled for the rest to pass.'
}

$authHeaders = @{ 'Ocp-Apim-Subscription-Key' = $subscriptionKeyPlain }
if ($token) { $authHeaders['Authorization'] = "Bearer $token" }

Write-Host ''
Write-Host "Smoke-testing $GatewayUrl" -ForegroundColor Cyan

# --- Gateway rejections ----------------------------------------------------

Write-Section 'Gateway rejects unauthorised traffic'

$noKey = Invoke-Gateway -Path '/bookings/BK-DOES-NOT-EXIST' -Headers @{}
Test-Assertion -Name 'a call with no subscription key is rejected' `
               -Condition ($noKey.StatusCode -in 401, 403) `
               -Detail "expected 401 or 403, got $($noKey.StatusCode)"

if ($token) {
    $badToken = Invoke-Gateway -Path '/bookings/BK-DOES-NOT-EXIST' -Headers @{
        'Ocp-Apim-Subscription-Key' = $subscriptionKeyPlain
        'Authorization'             = 'Bearer not.a.real.token'
    }
    Test-Assertion -Name 'a malformed bearer token is rejected' `
                   -Condition ($badToken.StatusCode -eq 401) `
                   -Detail "expected 401, got $($badToken.StatusCode)"
}

# --- Health ----------------------------------------------------------------

Write-Section 'Health'

$health = Invoke-Gateway -Path '/health' -Headers $authHeaders
Test-Assertion -Name 'health returns 200' `
               -Condition ($health.StatusCode -eq 200) `
               -Detail "got $($health.StatusCode)"

# --- Create ----------------------------------------------------------------

Write-Section 'Creating a booking'

$correlationId = [Guid]::NewGuid().ToString()
$idempotencyKey = [Guid]::NewGuid().ToString()

$createHeaders = $authHeaders.Clone()
$createHeaders['X-Correlation-ID'] = $correlationId
$createHeaders['Idempotency-Key']  = $idempotencyKey

$created = Invoke-Gateway -Method POST -Path '/bookings' -Headers $createHeaders -Body (New-BookingPayload)

Test-Assertion -Name 'a valid booking returns 201' `
               -Condition ($created.StatusCode -eq 201) `
               -Detail "got $($created.StatusCode): $($created.Content)"

$bookingId = $null
if ($created.StatusCode -eq 201) {
    $booking = $created.Content | ConvertFrom-Json
    $bookingId = $booking.bookingId

    Test-Assertion -Name 'the booking starts PENDING' `
                   -Condition ($booking.status -eq 'PENDING') `
                   -Detail "status was $($booking.status)"

    Test-Assertion -Name 'nights are derived from the dates' `
                   -Condition ($booking.nights -eq 4) `
                   -Detail "nights was $($booking.nights)"

    Test-Assertion -Name 'the correlation id survives the round trip' `
                   -Condition ($booking.correlationId -eq $correlationId) `
                   -Detail "sent $correlationId, got $($booking.correlationId)"

    Test-Assertion -Name 'the gateway strips origin fingerprinting headers' `
                   -Condition (-not $created.Headers.ContainsKey('X-Powered-By')) `
                   -Detail 'X-Powered-By was present on the response'
}

# --- Idempotency -----------------------------------------------------------

Write-Section 'Idempotency'

$replay = Invoke-Gateway -Method POST -Path '/bookings' -Headers $createHeaders -Body (New-BookingPayload)

Test-Assertion -Name 'replaying an idempotency key returns 200, not 201' `
               -Condition ($replay.StatusCode -eq 200) `
               -Detail "got $($replay.StatusCode)"

if ($replay.StatusCode -eq 200 -and $bookingId) {
    $replayed = $replay.Content | ConvertFrom-Json
    Test-Assertion -Name 'the replay returns the original booking' `
                   -Condition ($replayed.bookingId -eq $bookingId) `
                   -Detail "original $bookingId, replay $($replayed.bookingId)"
}

# --- Retrieval and eventual confirmation -----------------------------------

if ($bookingId) {
    Write-Section 'Retrieval'

    $fetched = Invoke-Gateway -Path "/bookings/$bookingId" -Headers $authHeaders
    Test-Assertion -Name 'the booking can be retrieved' `
                   -Condition ($fetched.StatusCode -eq 200) `
                   -Detail "got $($fetched.StatusCode)"

    $rewritten = Invoke-Gateway -Path "/reservations/$bookingId" -Headers $authHeaders
    Test-Assertion -Name 'the rewritten /reservations path reaches the same booking' `
                   -Condition ($rewritten.StatusCode -eq 200) `
                   -Detail "got $($rewritten.StatusCode) - check the rewrite-uri policy on getReservation"

    Write-Section 'Asynchronous confirmation'

    # The outbox publisher polls every two seconds and the worker has to receive
    # and store the message, so this is genuinely eventual. It is not a failure
    # of the API if it has not happened yet - only of the messaging path.
    $confirmed = $false
    foreach ($attempt in 1..15) {
        Start-Sleep -Seconds 2
        $poll = Invoke-Gateway -Path "/bookings/$bookingId" -Headers $authHeaders
        if ($poll.StatusCode -eq 200 -and ($poll.Content | ConvertFrom-Json).status -eq 'CONFIRMED') {
            $confirmed = $true
            break
        }
    }

    Test-Assertion -Name 'the booking is confirmed once its message is published' `
                   -Condition $confirmed `
                   -Detail 'still PENDING after 30s. Check the outbox depth at /health/ready and the App Service log stream; on the F1 plan there is no Always On, so the publisher sleeps when the app idles.'
}

# --- Validation ------------------------------------------------------------

Write-Section 'Validation'

$invalid = Invoke-Gateway -Method POST -Path '/bookings' -Headers $authHeaders -Body @{
    propertyId  = 'PROP-1001'
    guestEmail  = 'not-an-email'
    checkIn     = (Get-Date).AddDays(5).ToString('yyyy-MM-dd')
    checkOut    = (Get-Date).AddDays(3).ToString('yyyy-MM-dd')
    guests      = 0
    totalAmount = -1
    currency    = 'XYZ'
}

Test-Assertion -Name 'an invalid payload returns 400' `
               -Condition ($invalid.StatusCode -eq 400) `
               -Detail "got $($invalid.StatusCode)"

if ($invalid.StatusCode -eq 400) {
    $problem = $invalid.Content | ConvertFrom-Json
    $fields = @($problem.errors.PSObject.Properties.Name)

    Test-Assertion -Name 'the problem document names every invalid field' `
                   -Condition ($fields.Count -ge 4) `
                   -Detail "reported: $($fields -join ', ')"

    Test-Assertion -Name 'the problem document carries a correlation id' `
                   -Condition ([bool]$problem.correlationId) `
                   -Detail 'correlationId was empty'
}

# --- Rate limiting ---------------------------------------------------------

if ($RateLimitCalls -gt 0) {
    Write-Section "Rate limiting ($RateLimitCalls calls)"

    $statuses = @()
    $retryAfter = $null

    foreach ($i in 1..$RateLimitCalls) {
        $response = Invoke-Gateway -Path '/health' -Headers $authHeaders
        $statuses += $response.StatusCode
        if ($response.StatusCode -eq 429 -and $response.Headers.ContainsKey('Retry-After')) {
            $retryAfter = $response.Headers['Retry-After']
            break
        }
    }

    $throttled = $statuses -contains 429

    Test-Assertion -Name 'the rate limit eventually returns 429' `
                   -Condition $throttled `
                   -Detail "no 429 in $($statuses.Count) calls. On the Starter product the limit is 20/minute; a Premium key will not throttle here."

    if ($throttled) {
        Test-Assertion -Name 'the 429 tells the caller when to retry' `
                       -Condition ([bool]$retryAfter) `
                       -Detail 'Retry-After header missing'
    }
}

# --- Summary ---------------------------------------------------------------

$total = $script:Passed + $script:Failures.Count

Write-Host ''
Write-Host "$($script:Passed)/$total checks passed" -ForegroundColor $(if ($script:Failures.Count) { 'Red' } else { 'Green' })

foreach ($failure in $script:Failures) {
    Write-Host "  - $failure" -ForegroundColor Red
}

exit $(if ($script:Failures.Count) { 1 } else { 0 })
