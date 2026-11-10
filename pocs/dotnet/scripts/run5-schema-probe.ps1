<#
.SYNOPSIS
  Rodada 5 (E4): emite em HOMOLOGACAO SEFAZ-SP uma NFC-e com os campos opcionais do
  010g (vUnComLiq, vProdLiq, vProdLiqTot) para verificar qual pacote de schema a SP aplica.

.DESCRIPTION
  Roteiro e leitura do resultado: pocs/evidence/homologation.md, "Run 5".
  - Somente homologacao (o gerador da POC so produz tpAmb=2).
  - Valores dos campos novos sao placeholders (copia de vUnCom/vProd); nao e regra fiscal.
  - Senha do A1 pedida em tela (SecureString), colocada em RRF5_A1_PASSWORD so durante
    a execucao e removida no final, mesmo em caso de erro.
  - Issuer, A1 e todas as saidas ficam FORA do repositorio.
  - Transmite so se a validacao local contra o 010g passar e o operador confirmar.
  - Em resultado incerto (exit 3), consulta o MESMO documento; nunca regera nem reenvia.
  - Nao imprime chave de acesso, CNPJ nem XML.

.EXAMPLE
  .\run5-schema-probe.ps1 -IssuerFile C:\rrf5-private\issuer.json -PfxPath C:\rrf5-private\a1.pfx `
      -OutDir C:\rrf5-private\run5 -PackageZip C:\Users\rob_b\rrf5-private\sefaz\schemas\PL_010g_v1.03.zip `
      -XsdPath C:\Users\rob_b\rrf5-private\sefaz\schemas\schemas-010g\NFe\nfe_v4.00.xsd
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $IssuerFile,
    [Parameter(Mandatory)] [string] $PfxPath,
    [Parameter(Mandatory)] [string] $OutDir,
    [Parameter(Mandatory)] [string] $PackageZip,
    [Parameter(Mandatory)] [string] $XsdPath,
    [int] $NNF = 2,
    # Ensaio: gera, assina e valida, mas nunca transmite (arquivos com prefixo "ensaio-").
    [switch] $NoTransmit
)

$ErrorActionPreference = 'Stop'
$pocDir   = Resolve-Path (Join-Path $PSScriptRoot '..')
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$project  = Join-Path $pocDir 'Rrf5.Fiscal.Poc.csproj'
$fixtures = Join-Path $repoRoot 'pocs\fixtures'

function Stop-Run([string] $message) { Write-Host "PARADO: $message" -ForegroundColor Red; exit 1 }

function Invoke-Poc([string[]] $arguments) {
    $text = & dotnet run -c Release --no-build --project $project -- @arguments | Out-String
    $code = $LASTEXITCODE
    $json = $null
    try { $json = $text | ConvertFrom-Json } catch { $json = $null }
    if ($null -eq $json) { Stop-Run "a POC falhou em '$($arguments[0])' (exit $code); o motivo esta na mensagem JSON acima." }
    return [pscustomobject]@{ Code = $code; Json = $json }
}

# --- Verificacoes antes de tocar no A1 ---
foreach ($f in @($IssuerFile, $PfxPath, $PackageZip, $XsdPath)) {
    if (-not (Test-Path -LiteralPath $f -PathType Leaf)) { Stop-Run "arquivo nao encontrado: $f" }
}
foreach ($f in @($IssuerFile, $PfxPath, $OutDir)) {
    $full = [IO.Path]::GetFullPath($f)
    if ($full.StartsWith($repoRoot, [StringComparison]::OrdinalIgnoreCase)) { Stop-Run "$f esta dentro do repositorio." }
}
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$IssuerFile = (Resolve-Path -LiteralPath $IssuerFile).Path
$PfxPath    = (Resolve-Path -LiteralPath $PfxPath).Path
$PackageZip = (Resolve-Path -LiteralPath $PackageZip).Path
$XsdPath    = (Resolve-Path -LiteralPath $XsdPath).Path
$OutDir    = (Resolve-Path $OutDir).Path
$prefix    = if ($NoTransmit) { 'ensaio-' } else { '' }
$unsigned  = Join-Path $OutDir "${prefix}probe-nnf$NNF.xml"
$signed    = Join-Path $OutDir "${prefix}probe-nnf$NNF.signed.xml"
$proc      = Join-Path $OutDir "${prefix}probe-nnf$NNF.proc.xml"
if (-not $NoTransmit -and (Test-Path -LiteralPath $signed)) {
    Stop-Run "$signed ja existe: o nNF $NNF pode ja ter sido transmitido. Consulte esse arquivo (consult) em vez de gerar outro."
}

Write-Host "Compilando a POC..."
& dotnet build $project -c Release -nologo -v q | Out-Null
if ($LASTEXITCODE -ne 0) { Stop-Run 'build falhou.' }

# Senha: usa RRF5_A1_PASSWORD se ja estiver definida nesta sessao; senao pede em tela.
# Em ambos os casos a variavel e removida no final.
$bstr = [IntPtr]::Zero
if (-not $env:RRF5_A1_PASSWORD) {
    $secure = Read-Host -AsSecureString 'Senha do A1'
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
}
try {
    if ($bstr -ne [IntPtr]::Zero) { $env:RRF5_A1_PASSWORD = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }

    # 1. Gerar (cNF aleatorio de 8 digitos, dhEmi agora)
    $cnf = '{0:D8}' -f (Get-Random -Minimum 1 -Maximum 99999999)
    $dh  = (Get-Date).ToString('yyyy-MM-ddTHH:mm:sszzz')
    $r = Invoke-Poc @('generate', '--case', (Join-Path $fixtures 'nfce-minimal.json'),
        '--profile', (Join-Path $fixtures 'fiscal-profile-sp-sn-own-production-v1.json'),
        '--issuer', $IssuerFile, '--nnf', "$NNF", '--cnf', $cnf, '--dhemi', $dh,
        '--out', $unsigned, '--schema-probe', 'net-values-010g')
    if ($r.Code -ne 0 -or -not $r.Json.schemaProbe) { Stop-Run "generate: $($r.Json.error) $($r.Json.message)" }
    Write-Host "generate OK ($($r.Json.bytes) bytes)"

    # 2. Assinar
    $r = Invoke-Poc @('sign', '--pfx', $PfxPath, '--xml', $unsigned, '--out', $signed)
    if ($r.Code -ne 0 -or -not $r.Json.signatureVerified -or -not $r.Json.mutationRejected) { Stop-Run "sign: $($r.Json.error) $($r.Json.message)" }
    $signedSha = (Get-FileHash -Algorithm SHA256 -LiteralPath $signed).Hash.ToLowerInvariant()
    Write-Host "sign OK (A1 $($r.Json.certificateSha256.Substring(0,8))..., arquivo $($signedSha.Substring(0,8))...)"

    # 3. Validar contra o 010g (pacote com hash fixado na POC)
    $r = Invoke-Poc @('validate', '--xml', $signed, '--xsd', $XsdPath, '--package', $PackageZip)
    if (-not $r.Json.valid) { Stop-Run "validate: $($r.Json.error) $($r.Json.message) $($r.Json.messages -join '; ')" }
    Write-Host "validate 010g OK"

    # 4. Confirmar e transmitir
    if ($NoTransmit) { Write-Host "ENSAIO concluido: nada transmitido. Arquivos ${prefix}* em $OutDir"; exit 0 }
    $answer = Read-Host "Transmitir nNF $NNF para a HOMOLOGACAO SEFAZ-SP? (s/N)"
    if ($answer -ne 's') { Write-Host 'Nada transmitido. Os arquivos ficam em' $OutDir; exit 0 }

    $sentAt = Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz'
    $r = Invoke-Poc @('authorize', '--xml', $signed, '--pfx', $PfxPath, '--out', $proc)
    if ($r.Code -eq 3) {
        Write-Host 'Resultado incerto: consultando o MESMO documento (nao reenviar)...' -ForegroundColor Yellow
        $r = Invoke-Poc @('consult', '--xml', $signed, '--pfx', $PfxPath, '--out', $proc)
    }
    $j = $r.Json
}
finally {
    Remove-Item Env:RRF5_A1_PASSWORD -ErrorAction SilentlyContinue
    if ($bstr -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}

# --- Resumo para registrar em homologation.md (sem chave, CNPJ ou XML) ---
$retStat  = $j.sefaz.cStat
$protStat = $j.protocol.cStat
$reading = if ($protStat -eq '100' -or $retStat -eq '100') { 'SP homologacao aceita o 010g: E4 fecha para homologacao.' }
           elseif ($retStat -eq '225' -or $protStat -eq '225') { 'SP homologacao ainda NAO aceita o 010g (atrasada em relacao a NT). Registrar e reconferir antes de 16/11.' }
           elseif ($j.outcome -eq 'RESULT_UNKNOWN') { 'Ainda incerto: rodar consult de novo mais tarde com o mesmo arquivo.' }
           else { 'Outra rejeicao: o schema passou, portanto o 010g esta ativo. Registrar o codigo; nao transformar em regra fiscal.' }

Write-Host ''
Write-Host '===== Rodada 5 - resumo para homologation.md =====' -ForegroundColor Cyan
Write-Host "Data/hora envio : $sentAt"
Write-Host "Comando final   : $($j.command) -> $($j.outcome) (HTTP $($j.trace.httpStatus), $($j.trace.elapsedMs) ms)"
Write-Host "verAplic        : $($j.sefaz.verAplic)"
Write-Host "Lote/retorno    : $retStat - $($j.sefaz.xMotivo)"
if ($j.protocol) {
    Write-Host "Protocolo       : $protStat - $($j.protocol.xMotivo); nProt $($j.protocol.nProt); chave/digVal conferem: $($j.protocol.keyMatches)/$($j.protocol.digestMatches)"
}
Write-Host "SHA-256 assinado: $signedSha"
Write-Host "Leitura         : $reading"
Write-Host "Arquivos em     : $OutDir (fora do Git)"
