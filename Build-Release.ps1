param(
    [string]$Version = "1.2.0"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$artifacts = Join-Path $root "artifacts"
$objDir = Join-Path $artifacts "obj"
$packageDir = Join-Path $artifacts "package"
$releaseDir = Join-Path $artifacts "release"
$appDir = Join-Path $packageDir "StarcraftMapUnprotector"
$zipPath = Join-Path $releaseDir "StarcraftMapUnprotector-v$Version-win.zip"

$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe"
if (-not (Test-Path -LiteralPath $csc)) {
    throw "C# compiler not found: $csc"
}

Remove-Item -LiteralPath $objDir, $packageDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $objDir, $appDir, $releaseDir | Out-Null

$assemblyVersion = if ($Version -match '^\d+\.\d+\.\d+$') { "$Version.0" } else { $Version }
$assemblyInfo = Join-Path $objDir "AssemblyInfo.g.cs"
@"
using System.Reflection;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("StarcraftMapUnprotector")]
[assembly: AssemblyProduct("StarcraftMapUnprotector")]
[assembly: AssemblyDescription("StarCraft map unprotector")]
[assembly: AssemblyCompany("")]
[assembly: AssemblyCopyright("")]
[assembly: ComVisible(false)]
[assembly: AssemblyVersion("$assemblyVersion")]
[assembly: AssemblyFileVersion("$assemblyVersion")]
[assembly: AssemblyInformationalVersion("$Version")]
"@ | Set-Content -LiteralPath $assemblyInfo -Encoding UTF8

$sources = @(
    "Program.cs",
    "MpqExtractor.cs",
    "ChkParser.cs",
    "ChkNormalizer.cs",
    "TerrainRepairer.cs",
    "FreezeDecryptor.cs",
    "FreezeStaticRestorer.cs",
    "FreezeKeyRecovery.cs",
    "TriggerDump.cs",
    "Report.cs",
    "SoundInjector.cs"
) | ForEach-Object { Join-Path $root $_ }

$exePath = Join-Path $appDir "StarcraftMapUnprotector.exe"
$tkmpqRef = Join-Path $root "TkMPQLib.dll"
& $csc `
    /nologo `
    /target:exe `
    /platform:x86 `
    /optimize+ `
    /out:$exePath `
    "/reference:$tkmpqRef" `
    $sources `
    $assemblyInfo

if ($LASTEXITCODE -ne 0) {
    throw "Build failed with exit code $LASTEXITCODE"
}

Copy-Item -LiteralPath (Join-Path $root "TkMPQLib.dll"), (Join-Path $root "SComp.dll"), (Join-Path $root "Unprotect-All.ps1"), (Join-Path $root "README.md") -Destination $appDir
New-Item -ItemType Directory -Force -Path (Join-Path $appDir "Maps\Originals"), (Join-Path $appDir "Maps\Outputs") | Out-Null

if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

Compress-Archive -Path (Join-Path $appDir "*") -DestinationPath $zipPath -CompressionLevel Optimal

Write-Host "Built $exePath"
Write-Host "Packaged $zipPath"
