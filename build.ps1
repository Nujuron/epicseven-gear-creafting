param(
    [ValidatePattern('^[A-Za-z0-9_-]+\.exe$')]
    [string] $ExecutableName = 'E7Crafting-Simple.exe'
)

$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$kitRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\UnionMetadata'
$metadata = Get-ChildItem -LiteralPath $kitRoot -Directory |
    Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' } |
    Sort-Object { [version]$_.Name } -Descending |
    ForEach-Object { Join-Path $_.FullName 'Windows.winmd' } |
    Where-Object { Test-Path -LiteralPath $_ } |
    Select-Object -First 1
$runtime = Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL\System.Runtime\v4.0_4.0.0.0__b03f5f7f11d50a3a\System.Runtime.dll'
if (-not $metadata) { throw 'Windows SDK metadata is required to build the app.' }
$output = Join-Path $PSScriptRoot 'outputs'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$exe = Join-Path $output $ExecutableName
$sources = Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'desktop') -Filter '*.cs'
& (Join-Path $framework 'csc.exe') /nologo /target:winexe /platform:x64 /optimize+ "/out:$exe" /r:System.Drawing.dll /r:System.Windows.Forms.dll "/r:$runtime" "/r:$framework\System.Runtime.WindowsRuntime.dll" "/r:$metadata" @($sources.FullName)
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Write-Output $exe
