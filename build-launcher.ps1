[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$compilerCandidates = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
)
$compiler = $compilerCandidates |
    Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
    Select-Object -First 1

if ($null -eq $compiler) {
    throw 'The .NET Framework C# compiler (csc.exe) was not found.'
}

$source = Join-Path $PSScriptRoot 'UnlockAnimationLauncher.cs'
$manifest = Join-Path $PSScriptRoot 'UnlockAnimationLauncher.exe.manifest'
$output = Join-Path $PSScriptRoot 'UnlockAnimationLauncher.exe'
$residentSource = Join-Path $PSScriptRoot 'UnlockAnimationResident.cs'
$residentManifest = Join-Path $PSScriptRoot 'UnlockAnimationResident.exe.manifest'
$residentOutput = Join-Path $PSScriptRoot 'UnlockAnimationResident.exe'

foreach ($inputFile in @(
    $source,
    $manifest,
    $residentSource,
    $residentManifest)) {
    if (-not (Test-Path -LiteralPath $inputFile -PathType Leaf)) {
        throw "Missing launcher build input: $inputFile"
    }
}

& $compiler @(
    '/nologo',
    '/target:winexe',
    '/platform:x64',
    '/optimize+',
    '/reference:System.dll',
    '/reference:System.Core.dll',
    '/reference:System.Drawing.dll',
    '/reference:System.Windows.Forms.dll',
    "/win32manifest:$manifest",
    "/out:$output",
    $source
)

if ($LASTEXITCODE -ne 0) {
    throw "One-shot launcher compilation failed with exit code $LASTEXITCODE."
}

& $compiler @(
    '/nologo',
    '/target:winexe',
    '/platform:x64',
    '/optimize+',
    '/reference:System.dll',
    '/reference:System.Core.dll',
    '/reference:System.Drawing.dll',
    '/reference:System.Windows.Forms.dll',
    "/win32manifest:$residentManifest",
    "/out:$residentOutput",
    $residentSource
)

if ($LASTEXITCODE -ne 0) {
    throw "Resident launcher compilation failed with exit code $LASTEXITCODE."
}

Get-Item -LiteralPath $output, $residentOutput |
    Select-Object FullName, Length, LastWriteTime
