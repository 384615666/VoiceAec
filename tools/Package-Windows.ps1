param(
    [Parameter(Mandatory = $true)][string] $OutputDirectory,
    [Parameter(Mandatory = $true)][string] $NativeBin,
    [Parameter(Mandatory = $true)][string] $PluginDll,
    [Parameter(Mandatory = $true)][string] $ModelsDirectory,
    [Parameter(Mandatory = $true)][string] $LocaleDirectory,
    [Parameter(Mandatory = $true)][string] $RuntimeDirectory,
    [string] $Version = '0.4.0',   # OBS plugin version written into obs-plugin\manifest.json (independent of the desktop app version)
    [string] $DotNetExe = (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe')
)
$ErrorActionPreference = 'Stop'
$project = Join-Path (Split-Path $PSScriptRoot -Parent) 'VoiceAec.csproj'
$output = [IO.Path]::GetFullPath($OutputDirectory)
foreach ($source in @($PluginDll, (Join-Path $NativeBin 'liblocalvqe.dll'),
    (Join-Path $LocaleDirectory 'en-US.ini'), (Join-Path $ModelsDirectory 'localvqe-v1.3-4.8M-f32.gguf'))) {
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing resource: $source" }
}
& $DotNetExe publish $project -c Release -r win-x64 --self-contained true -o $output --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
$plugin = Join-Path $output 'obs-plugin'
New-Item -ItemType Directory -Path $plugin, (Join-Path $plugin 'locale'), (Join-Path $output 'models') -Force | Out-Null
$entries = [Collections.Generic.List[object]]::new()
function Add-PayloadFile([string] $source, [string] $relative, [string] $destination) {
    $target = Join-Path $output $relative
    Copy-Item -LiteralPath $source -Destination $target -Force
    $entries.Add([ordered]@{
        Source = $relative.Replace('\', '/'); Destination = $destination.Replace('\', '/')
        Sha256 = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
    })
}
foreach ($file in (Get-ChildItem -LiteralPath $NativeBin -File -Filter '*.dll' |
    Where-Object { $_.Name -eq 'liblocalvqe.dll' -or $_.Name -like 'ggml*.dll' })) {
    Add-PayloadFile $file.FullName $file.Name ('bin/64bit/' + $file.Name)
}
foreach ($name in @('libgcc_s_seh-1.dll', 'libstdc++-6.dll', 'libwinpthread-1.dll')) {
    Add-PayloadFile (Join-Path $RuntimeDirectory $name) $name ('bin/64bit/' + $name)
}
Add-PayloadFile $PluginDll 'obs-plugin/obs-localvqe.dll' 'bin/64bit/obs-localvqe.dll'
foreach ($file in (Get-ChildItem -LiteralPath $ModelsDirectory -File -Filter '*.gguf')) {
    Add-PayloadFile $file.FullName ('models/' + $file.Name) ('data/' + $file.Name)
}
foreach ($file in (Get-ChildItem -LiteralPath $LocaleDirectory -File -Filter '*.ini')) {
    Add-PayloadFile $file.FullName ('obs-plugin/locale/' + $file.Name) ('data/locale/' + $file.Name)
}
[ordered]@{ Schema = 1; Version = $Version; Files = $entries.ToArray() } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $plugin 'manifest.json') -Encoding utf8

# Documentation payload: keep README links, screenshots and notices working inside the package.
$repoRoot = Split-Path $project -Parent
$docsOut = Join-Path $output 'docs'
New-Item -ItemType Directory -Path $docsOut -Force | Out-Null

foreach ($file in @('README.md', 'README.en.md', 'LICENSE', 'THIRD-PARTY-NOTICES.md')) {
    $source = Join-Path $repoRoot $file
    if (Test-Path -LiteralPath $source -PathType Leaf) {
        Copy-Item -LiteralPath $source -Destination $output -Force
    }
}
$v14Doc = Join-Path $repoRoot 'docs\V14-AEC-NUMERICS.md'
if (Test-Path -LiteralPath $v14Doc -PathType Leaf) {
    Copy-Item -LiteralPath $v14Doc -Destination $docsOut -Force
}
$docsImages = Join-Path $repoRoot 'docs\images'
if (Test-Path -LiteralPath $docsImages -PathType Container) {
    Copy-Item -LiteralPath $docsImages -Destination (Join-Path $docsOut 'images') -Recurse -Force
}
$nativeDir = Join-Path $repoRoot 'native'
if (Test-Path -LiteralPath $nativeDir -PathType Container) {
    Copy-Item -LiteralPath $nativeDir -Destination (Join-Path $output 'native') -Recurse -Force
}

Write-Output "Unified desktop and OBS package: $output"
