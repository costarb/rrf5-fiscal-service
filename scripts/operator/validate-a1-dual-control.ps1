<#
.SYNOPSIS
  Valida, na stack LOCAL, o A1 real contra a cadeia ICP-Brasil (com LCR online) e o
  controle duplo de ativacao com duas aplicacoes de perfis distintos (TM-03).

.DESCRIPTION
  Roteiro e leitura do resultado: pocs/evidence/threat-model.md, secao TM-03.
  - Somente a stack local de desenvolvimento (deploy/compose). Nada vai para a SEFAZ.
  - O A1, o arquivo do emitente e a saida ficam FORA do repositorio.
  - A senha do A1 e pedida em tela (SecureString) e so existe em memoria durante o envio
    (ou vem de RRF5_A1_PASSWORD, removida logo apos a leitura).
  - O A1 fica no OpenBao local em modo dev (memoria; some quando o container reinicia)
    e os metadados no banco local. Nenhum CNPJ, fingerprint ou senha e impresso.
  - Perfis: "gestor" (issuer.write + certificates.manage) envia; "aprovador"
    (certificates.approve) ativa. Os dois perfis nunca vao na mesma aplicacao.

  Verificacoes (cada uma PASSA ou FALHA):
    1. senha errada e recusada (certificate.invalid)
    2. o A1 real encadeia numa raiz ICP-Brasil e nenhuma LCR o revoga (201, PENDING_APPROVAL)
    3. o gestor nao consegue ativar o proprio envio (403)
    4. o aprovador sem concessao do estabelecimento nao ve o certificado (404)
    5. o aprovador com concessao ativa (200, ACTIVE) e a aprovacao fica auditada
    6. o mesmo certificado nao pode ser ativado de novo (409)

.EXAMPLE
  .\scripts\operator\validate-a1-dual-control.ps1 `
      -IssuerFile C:\Users\rob_b\rrf5-private\issuer-sp-real.json `
      -PfxPath C:\Users\rob_b\rrf5-private\<arquivo>.pfx
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $IssuerFile,
    [Parameter(Mandatory)] [string] $PfxPath,
    [string] $BundleZip = 'C:\Users\rob_b\rrf5-private\icp-brasil\ACcompactado.zip',
    [int] $Crt = 1,
    [string] $ApiUrl = 'http://localhost:58090',
    [string] $OwnerConnection = 'Host=127.0.0.1;Port=55432;Username=rrf5_owner;Password=local-dev-only;Database=rrf5_fiscal'
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$worker = Join-Path $repoRoot 'src\Rrf5.Fiscal.Worker\bin\Release\net10.0\Rrf5.Fiscal.Worker.dll'
$apiDir = Join-Path $repoRoot 'src\Rrf5.Fiscal.Api\bin\Release\net10.0'
$results = New-Object System.Collections.Generic.List[object]

function Stop-Run([string] $message) { throw "PARADO: $message" }

function Add-Check([string] $name, [bool] $ok, [string] $detail) {
    $results.Add([pscustomobject]@{ Verificacao = $name; Resultado = $(if ($ok) { 'PASSA' } else { 'FALHA' }); Detalhe = $detail })
    $color = if ($ok) { 'Green' } else { 'Red' }
    Write-Host ("[{0}] {1} - {2}" -f $(if ($ok) { 'PASSA' } else { 'FALHA' }), $name, $detail) -ForegroundColor $color
}

function Invoke-Api([string] $method, [string] $path, [string] $token, $body, [switch] $Form) {
    $headers = @{ 'Idempotency-Key' = [guid]::NewGuid().ToString('N') }
    if ($token) { $headers['Authorization'] = "Bearer $token" }
    $params = @{ Method = $method; Uri = "$ApiUrl$path"; Headers = $headers; UseBasicParsing = $true }
    if ($Form) { $params['Body'] = $body; $params['ContentType'] = 'application/x-www-form-urlencoded' }
    elseif ($null -ne $body) { $params['Body'] = [Text.Encoding]::UTF8.GetBytes(($body | ConvertTo-Json -Depth 6 -Compress)); $params['ContentType'] = 'application/json' }
    try {
        $response = Invoke-WebRequest @params
        $status = [int] $response.StatusCode; $text = $response.Content
    } catch [System.Net.WebException] {
        $r = $_.Exception.Response
        if ($null -eq $r) { Stop-Run "API inacessivel em $ApiUrl ($($_.Exception.Message))." }
        $status = [int] $r.StatusCode
        $text = (New-Object IO.StreamReader($r.GetResponseStream())).ReadToEnd()
    }
    $json = $null
    if ($text) { try { $json = $text | ConvertFrom-Json } catch { $json = $null } }
    return [pscustomobject]@{ Status = $status; Json = $json }
}

function Get-Token([string] $clientId, [string] $secret) {
    $r = Invoke-Api 'POST' '/oauth/token' $null "grant_type=client_credentials&client_id=$([uri]::EscapeDataString($clientId))&client_secret=$([uri]::EscapeDataString($secret))" -Form
    if ($r.Status -ne 200) { Stop-Run "token recusado ($($r.Status) $($r.Json.error))." }
    return $r.Json.access_token
}

function Expect([object] $r, [int] $status, [string] $what) {
    if ($r.Status -ne $status) { Stop-Run "$what retornou $($r.Status) $($r.Json.code) $($r.Json.detail)" }
    return $r.Json
}

try {
# --- Verificacoes antes de tocar no A1 ---
foreach ($f in @($IssuerFile, $PfxPath, $BundleZip)) {
    if (-not (Test-Path -LiteralPath $f -PathType Leaf)) { Stop-Run "arquivo nao encontrado: $f" }
}
foreach ($f in @($IssuerFile, $PfxPath)) {
    if ([IO.Path]::GetFullPath($f).StartsWith($repoRoot, [StringComparison]::OrdinalIgnoreCase)) { Stop-Run "$f esta dentro do repositorio." }
}
$issuer = Get-Content -LiteralPath $IssuerFile -Raw -Encoding UTF8 | ConvertFrom-Json
foreach ($field in @('cnpj', 'name', 'address')) { if (-not $issuer.$field) { Stop-Run "o arquivo do emitente nao tem '$field'." } }

} catch { Write-Host $_.Exception.Message -ForegroundColor Red; exit 1 }

Write-Host '== Compilando' -ForegroundColor Cyan
& dotnet build (Join-Path $repoRoot 'Rrf5.Fiscal.slnx') -c Release -v quiet -nologo | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Host 'PARADO: o build falhou.' -ForegroundColor Red; exit 1 }

Write-Host '== Preparando a stack local (migracoes, logins locais, catalogo, pacote ICP-Brasil)' -ForegroundColor Cyan
$env:DOTNET_ENVIRONMENT = 'Development'
$env:ConnectionStrings__Owner = $OwnerConnection
$env:RRF5_LOCAL_APP_PASSWORD = 'local-dev-only'
$api = $null
$plain = $null
try {
    Push-Location (Split-Path $worker)
    & dotnet $worker migrate | Out-Null; if ($LASTEXITCODE -ne 0) { Stop-Run 'migrate falhou (a stack local esta de pe?).' }
    & dotnet $worker local-app-login | Out-Null
    & dotnet $worker load-catalog | Out-Null
    $stored = (& dotnet $worker store-artifact $BundleZip icp-brasil-ac-bundle) | Out-String
    if ($stored -notmatch 'stored icp-brasil-ac-bundle') { Stop-Run "o pacote ICP-Brasil nao confere com o catalogo: $stored" }
    $boot = (& dotnet $worker bootstrap-platform-admin) | Out-String
    Pop-Location
    $platformId = [regex]::Match($boot, 'client_id=(\S+)').Groups[1].Value
    $platformSecret = [regex]::Match($boot, 'client_secret=(\S+)').Groups[1].Value
    if (-not $platformId -or -not $platformSecret) { Stop-Run 'bootstrap-platform-admin nao devolveu a credencial.' }
    Remove-Item Env:RRF5_LOCAL_APP_PASSWORD

    Write-Host "== Subindo a API local em $ApiUrl" -ForegroundColor Cyan
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $env:ASPNETCORE_URLS = $ApiUrl
    $log = Join-Path ([IO.Path]::GetTempPath()) ("rrf5-api-" + [guid]::NewGuid().ToString('N') + '.log')
    $api = Start-Process -FilePath 'dotnet' -ArgumentList 'Rrf5.Fiscal.Api.dll' -WorkingDirectory $apiDir -PassThru -WindowStyle Hidden -RedirectStandardOutput $log
    $up = $false
    for ($i = 0; $i -lt 40 -and -not $up; $i++) {
        Start-Sleep -Milliseconds 500
        try { $up = (Invoke-WebRequest "$ApiUrl/health/live" -UseBasicParsing -TimeoutSec 2).StatusCode -eq 200 } catch { $up = $false }
    }
    if (-not $up) { Stop-Run "a API nao respondeu em $ApiUrl (log: $log)." }

    Write-Host '== Criando tenant, perfis e emitente' -ForegroundColor Cyan
    $platform = Get-Token $platformId $platformSecret
    $stamp = Get-Date -Format 'yyyyMMdd-HHmm'
    $tenant = Expect (Invoke-Api 'POST' '/v1/tenants' $platform @{ name = "Validacao A1 $stamp" }) 201 'criar tenant'
    $admin = Expect (Invoke-Api 'POST' "/v1/tenants/$($tenant.id)/applications" $platform @{ name = 'Administrador'; scopes = @('tenant.admin') }) 201 'criar administrador'
    $adminToken = Get-Token $admin.clientId $admin.clientSecret
    $manager = Expect (Invoke-Api 'POST' '/v1/applications' $adminToken @{ name = 'Gestor de certificados'; scopes = @('issuer.write', 'certificates.manage') }) 201 'criar gestor'
    $approver = Expect (Invoke-Api 'POST' '/v1/applications' $adminToken @{ name = 'Aprovador de certificados'; scopes = @('certificates.approve') }) 201 'criar aprovador'
    $managerToken = Get-Token $manager.clientId $manager.clientSecret
    $approverToken = Get-Token $approver.clientId $approver.clientSecret

    $company = Expect (Invoke-Api 'POST' '/v1/companies' $managerToken @{ legalName = $issuer.name; taxId = $issuer.cnpj }) 201 'cadastrar empresa'
    $establishment = Expect (Invoke-Api 'POST' "/v1/companies/$($company.id)/establishments" $managerToken @{
        taxId = $issuer.cnpj; stateRegistration = $issuer.ie; uf = $issuer.address.uf; municipalityCode = $issuer.address.cityCode; crt = $Crt
        address = @{ street = $issuer.address.street; number = $issuer.address.number; district = $issuer.address.district; municipalityName = $issuer.address.city; postalCode = $issuer.address.zip }
    }) 201 'cadastrar estabelecimento'
    $environment = Expect (Invoke-Api 'POST' "/v1/establishments/$($establishment.id)/environments" $managerToken @{ environment = 'HOMOLOGATION' }) 201 'criar ambiente'

    Write-Host '== A1' -ForegroundColor Cyan
    $pkcs12 = [Convert]::ToBase64String([IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $PfxPath).Path))
    $wrong = Invoke-Api 'POST' "/v1/environments/$($environment.id)/certificates" $managerToken @{ pkcs12 = $pkcs12; password = ('errada-' + [guid]::NewGuid().ToString('N')) }
    Add-Check '1. senha errada recusada' ($wrong.Status -eq 422 -and $wrong.Json.code -eq 'certificate.invalid') "$($wrong.Status) $($wrong.Json.code)"

    if ($env:RRF5_A1_PASSWORD) {
        $plain = $env:RRF5_A1_PASSWORD
        Remove-Item Env:RRF5_A1_PASSWORD
    } else {
        $secure = Read-Host 'Senha do A1' -AsSecureString
        $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
        try { $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
    }
    $upload = Invoke-Api 'POST' "/v1/environments/$($environment.id)/certificates" $managerToken @{ pkcs12 = $pkcs12; password = $plain }
    $plain = $null
    $pkcs12 = $null
    $chainOk = $upload.Status -eq 201 -and $upload.Json.status -eq 'PENDING_APPROVAL'
    $detail = if ($chainOk) { "raiz: $($upload.Json.chainRoot); validade ate $(([datetime]$upload.Json.notAfter).ToString('yyyy-MM-dd'))" } else { "$($upload.Status) $($upload.Json.code): $($upload.Json.detail)" }
    Add-Check '2. cadeia ICP-Brasil e LCR' $chainOk $detail
    if (-not $chainOk) { throw 'interrompido: o A1 nao passou na cadeia; as verificacoes de controle duplo precisam de um envio aceito.' }
    $certificate = $upload.Json.id

    $self = Invoke-Api 'POST' "/v1/certificates/$certificate/approve" $managerToken $null
    Add-Check '3. gestor nao ativa o proprio envio' ($self.Status -eq 403) "$($self.Status) $($self.Json.code)"

    $ungranted = Invoke-Api 'POST' "/v1/certificates/$certificate/approve" $approverToken $null
    Add-Check '4. aprovador sem concessao nao ve o certificado' ($ungranted.Status -eq 404) "$($ungranted.Status) $($ungranted.Json.code)"

    Expect (Invoke-Api 'PUT' "/v1/applications/$($approver.id)/establishments/$($establishment.id)" $adminToken $null) 204 'conceder estabelecimento ao aprovador' | Out-Null
    $approved = Invoke-Api 'POST' "/v1/certificates/$certificate/approve" $approverToken $null
    $activeOk = $approved.Status -eq 200 -and $approved.Json.status -eq 'ACTIVE' -and $approved.Json.approverApplicationId -eq $approver.id
    Add-Check '5. aprovador ativa (auditado)' $activeOk "$($approved.Status) $($approved.Json.status)"

    $again = Invoke-Api 'POST' "/v1/certificates/$certificate/approve" $approverToken $null
    Add-Check '6. nao ativa duas vezes' ($again.Status -eq 409) "$($again.Status) $($again.Json.code)"
}
catch {
    Write-Host $_.Exception.Message -ForegroundColor Yellow
}
finally {
    $plain = $null
    Remove-Item Env:RRF5_LOCAL_APP_PASSWORD -ErrorAction SilentlyContinue
    Remove-Item Env:RRF5_A1_PASSWORD -ErrorAction SilentlyContinue
    if ($api -and -not $api.HasExited) { Stop-Process -Id $api.Id -Force }
    if ($up -and $log -and (Test-Path -LiteralPath $log)) { Start-Sleep -Milliseconds 300; Remove-Item -LiteralPath $log -Force -ErrorAction SilentlyContinue }
}

Write-Host ''
Write-Host '== Resumo (cole este quadro na conversa)' -ForegroundColor Cyan
$results | Format-Table -AutoSize | Out-String | Write-Host
Write-Host ("Executado em {0}; dados ficam so na stack local." -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz'))
if ($results.Count -lt 6 -or ($results | Where-Object Resultado -eq 'FALHA')) { exit 1 }
