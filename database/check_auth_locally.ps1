# Manual local check: credentials and tokens remain in process memory only.
# Do not use tracing, transcripts or HTTP debugging. Never pass secrets as arguments.
[CmdletBinding()]
param(
    [string]$ApiBase = 'http://localhost:5156'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
$apiUri = [Uri]$ApiBase
if (-not $apiUri.IsAbsoluteUri -or -not $apiUri.IsLoopback -or
    $apiUri.Scheme -notin @('http', 'https') -or $apiUri.UserInfo -or $apiUri.Query -or $apiUri.Fragment) {
    throw 'Only a loopback API URL without credentials, query or fragment is allowed.'
}

$handler = [System.Net.Http.HttpClientHandler]::new()
$handler.AllowAutoRedirect = $false
$client = [System.Net.Http.HttpClient]::new($handler)
$client.Timeout = [TimeSpan]::FromSeconds(20)
$script:checkStage = 'Initialize'
$script:lastHttpStatus = $null

function Write-SafeFailure {
    param([System.Exception]$Exception)
    Write-Host ('Failed stage: ' + $script:checkStage)
    if ($null -ne $script:lastHttpStatus) {
        Write-Host ('Last response HTTP: ' + $script:lastHttpStatus)
    }
    else { Write-Host 'Last response HTTP: unavailable (no response received)' }
    $cause = $Exception
    $depth = 0
    while ($null -ne $cause -and $depth -lt 16) {
        # Only fixed labels, runtime types and numeric/enum codes are safe to print.
        # Exception messages, stack traces and ErrorRecord may contain secret values.
        Write-Host ('Exception[' + $depth + ']: ' + $cause.GetType().FullName)
        if ($cause -is [System.Net.WebException]) {
            Write-Host ('Web status: ' + $cause.Status)
            if ($cause.Response -is [System.Net.HttpWebResponse]) {
                Write-Host ('Exception response HTTP: ' + [int]$cause.Response.StatusCode)
            }
        }
        if ($cause -is [System.Net.Sockets.SocketException]) {
            Write-Host ('Socket error: ' + $cause.SocketErrorCode)
        }
        # HttpRequestException.StatusCode exists on newer .NET, but not .NET Framework.
        $statusProperty = $cause.PSObject.Properties['StatusCode']
        if ($null -ne $statusProperty -and $null -ne $statusProperty.Value) {
            Write-Host ('Exception HTTP: ' + [int]$statusProperty.Value)
        }
        $cause = $cause.InnerException
        $depth++
    }
}

function Get-ApiStatus {
    param([string]$Method, [string]$AccessToken = '', [string]$Json = '')
    $script:lastHttpStatus = $null
    $script:checkStage = 'API.BuildRequest'
    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::new($Method),
        ($ApiBase.TrimEnd('/') + '/api/equipment'))
    try {
        if ($AccessToken) {
            $request.Headers.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $AccessToken)
        }
        if ($Json) {
            $request.Content = [System.Net.Http.StringContent]::new($Json, [System.Text.Encoding]::UTF8, 'application/json')
        }
        $script:checkStage = 'API.SendAsync'
        $sendTask = $client.SendAsync($request)
        $script:checkStage = 'API.AwaitResponse'
        $response = $sendTask.GetAwaiter().GetResult()
        $script:lastHttpStatus = [int]$response.StatusCode
        try { return [int]$response.StatusCode } finally { $response.Dispose() }
    }
    finally { $request.Dispose() }
}

function Test-User {
    param([string]$Label, [bool]$CheckForbiddenWrite)
    $email = Read-Host "Test user email ($Label)"
    $password = Read-Host 'Supabase Auth user password' -AsSecureString
    $plainPassword = $null
    $payload = $null
    $document = $null
    $accessToken = $null
    $request = $null
    $response = $null
    try {
        $script:lastHttpStatus = $null
        $script:checkStage = 'Auth.SecureStringToPassword'
        $plainPassword = [System.Net.NetworkCredential]::new('', $password).Password
        $script:checkStage = 'Auth.SerializeJson'
        $payload = @{ email = $email; password = $plainPassword } | ConvertTo-Json -Compress
        $script:checkStage = 'Auth.BuildRequest'
        $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Post,
            'https://cyzanurmyswswbicjiaf.supabase.co/auth/v1/token?grant_type=password')
        if (-not $request.Headers.TryAddWithoutValidation('apikey', $publishableKey)) {
            throw [InvalidOperationException]::new('API key header could not be added.')
        }
        $request.Content = [System.Net.Http.StringContent]::new($payload, [System.Text.Encoding]::UTF8, 'application/json')
        $script:checkStage = 'Auth.SendAsync'
        $sendTask = $client.SendAsync($request)
        $script:checkStage = 'Auth.AwaitResponse'
        $response = $sendTask.GetAwaiter().GetResult()
        $script:lastHttpStatus = [int]$response.StatusCode
        Write-Host "$Label Auth HTTP: $([int]$response.StatusCode)"
        if (-not $response.IsSuccessStatusCode) {
            Write-Host 'Sign-in failed. Response body hidden; check credentials, email confirmation, CAPTCHA/MFA and Auth settings locally.'
            return
        }
        $script:checkStage = 'Auth.ReadResponse'
        $responseText = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        $script:checkStage = 'Auth.ParseJson'
        $document = $responseText | ConvertFrom-Json
        $script:checkStage = 'Auth.ExtractAccessToken'
        $accessToken = $document.access_token
        if (-not $accessToken) { throw [InvalidOperationException]::new('Access token was not received.') }
        $status = Get-ApiStatus -Method GET -AccessToken $accessToken
        Write-Host "$Label GET HTTP: $status (expected 200 for an active profile with an allowed role)"
        if ($CheckForbiddenWrite) {
            # An empty object fails validation even if engineer was assigned by mistake.
            # This negative check does not create equipment.
            $status = Get-ApiStatus -Method POST -AccessToken $accessToken -Json '{}'
            Write-Host "$Label POST HTTP: $status (expected 403 for manager/maintenance_specialist)"
        }
    }
    finally {
        if ($response) { $response.Dispose() }
        if ($request) { $request.Dispose() }
        $password.Dispose()
        $plainPassword = $null; $payload = $null; $document = $null; $responseText = $null; $accessToken = $null; $email = $null
    }
}

try {
    Write-Host "Anonymous GET HTTP: $(Get-ApiStatus -Method GET) (expected 401)"
    Write-Host "Invalid token GET HTTP: $(Get-ApiStatus -Method GET -AccessToken 'invalid-token') (expected 401)"
    $publishableKey = Read-Host 'Project publishable API key (sb_publishable_..., NOT secret/service_role)'
    if (-not $publishableKey.StartsWith('sb_publishable_', [StringComparison]::Ordinal)) {
        throw [InvalidOperationException]::new('Only a publishable key in the new format is accepted.')
    }
    Test-User -Label 'Engineer' -CheckForbiddenWrite $false
    $checkOtherRole = Read-Host 'Check an existing manager/maintenance_specialist without engineer? [y/N]'
    if ($checkOtherRole -in @('y', 'yes')) {
        Test-User -Label 'Manager or maintenance_specialist' -CheckForbiddenWrite $true
    }
    else { Write-Host '403 check skipped: another user was not provided.' }
}
catch {
    # Never print exception details, response bodies or requests containing secrets.
    Write-SafeFailure -Exception $_.Exception
    exit 1
}
finally {
    $publishableKey = $null
    $client.Dispose()
    $handler.Dispose()
}
