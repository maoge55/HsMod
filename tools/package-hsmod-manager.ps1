param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$hsModProject = Join-Path $repoRoot "HsMod\HsMod.csproj"
$managerProject = Join-Path $repoRoot "HsModManager\HsModManager.csproj"
$distRoot = Join-Path $repoRoot "dist"
$publishDir = Join-Path $distRoot "HsModManager-$Runtime"
$zipPath = Join-Path $distRoot "HsModManager-$Runtime.zip"

$targetAssembly = "C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8\mscorlib.dll"
if (-not (Test-Path $targetAssembly)) {
    throw ".NET Framework 4.8 Developer Pack is required. Install it first, then rerun this script."
}

Write-Host "Building HsMod plugin..."
dotnet build $hsModProject -c $Configuration /p:PostBuildEvent=

Write-Host "Publishing HsModManager self-contained package..."
if (Test-Path $publishDir) {
    $resolvedPublish = [System.IO.Path]::GetFullPath($publishDir)
    if (-not $resolvedPublish.StartsWith($repoRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to delete outside repo: $resolvedPublish"
    }
    Remove-Item -LiteralPath $resolvedPublish -Recurse -Force
}

dotnet publish $managerProject `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -o $publishDir `
    /p:PublishSingleFile=true `
    /p:IncludeNativeLibrariesForSelfExtract=true `
    /p:EnableCompressionInSingleFile=true

$requiredFiles = @(
    (Join-Path $publishDir "HsModManager.exe"),
    (Join-Path $publishDir "Payload\HsMod.dll"),
    (Join-Path $publishDir "Payload\UnstrippedCorlib\mscorlib.dll"),
    (Join-Path $publishDir "README.zh-CN.txt")
)

foreach ($file in $requiredFiles) {
    if (-not (Test-Path $file)) {
        throw "Package is missing required file: $file"
    }
}

if (Test-Path $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

Write-Host "Creating zip package..."
Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath -Force

$zip = Get-Item $zipPath
Write-Host "Package created: $($zip.FullName) ($([Math]::Round($zip.Length / 1MB, 2)) MB)"
