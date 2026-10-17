#requires -Version 7.0
[CmdletBinding()]
param([switch]$SelfContained)
$ErrorActionPreference = 'Stop'

$PSNativeCommandUseErrorActionPreference = $false
$RepositoryRoot = Split-Path $PSScriptRoot -Parent
$artifacts = Join-Path $RepositoryRoot 'artifacts'
$packageName = ($SelfContained ? 'MusicClassificationUtility-win-x64-self-contained' : 'MusicClassificationUtility-win-x64')
$publish = Join-Path $artifacts $packageName
$includeRuntime = ($SelfContained ? 'true' : 'false')
& dotnet build (Join-Path $RepositoryRoot 'application\MusicClassificationUtility.slnx') -c Release --nologo -m:1

if ($LASTEXITCODE -ne 0) {
	throw 'Solution build failed.'
}

& dotnet run --project (Join-Path $RepositoryRoot 'application\MusicClassification.Tests') -c Release --no-build

if ($LASTEXITCODE -ne 0) {
	throw 'Core checks failed.'
}

if (Test-Path -LiteralPath $publish) {
	$resolved = (Resolve-Path -LiteralPath $publish).Path

	if ($resolved -ne [IO.Path]::GetFullPath((Join-Path $artifacts $packageName)) `
			-or -not $resolved.StartsWith([IO.Path]::GetFullPath($artifacts) + [IO.Path]::DirectorySeparatorChar)) {
		throw 'Unexpected publish directory.'
	}

	Remove-Item -LiteralPath $resolved -Recurse -Force
}

& dotnet publish (Join-Path $RepositoryRoot 'application\MusicClassification.GUI') `
	-c Release -r win-x64 --self-contained $includeRuntime -p:PublishSingleFile=true `
	-p:IncludeNativeLibrariesForSelfExtract=true -p:SatelliteResourceLanguages=en -p:DebugType=None `
	-p:DebugSymbols=false -o $publish --nologo

if ($LASTEXITCODE -ne 0) {
	throw 'Publish failed.'
}

Copy-Item -LiteralPath (Join-Path $RepositoryRoot 'LICENSE') -Destination $publish
Get-ChildItem -LiteralPath $publish -File | Where-Object { $_.Extension -in '.pdb', '.xml' } | ForEach-Object { Remove-Item -LiteralPath $_.FullName }
$forbidden = Get-ChildItem -LiteralPath $publish -Recurse -File `
	| Where-Object { $_.Extension -in '.ckpt', '.onnx', '.pb' -or $_.Name -eq 'runtime.local.json' }

if ($forbidden) {
	throw 'Unexpected runtime or development files in the release.'
}

$rootFiles = @(Get-ChildItem -LiteralPath $publish -File | Select-Object -ExpandProperty Name)

if (@($rootFiles | Where-Object { $_ -notin 'MusicClassification.GUI.exe', 'LICENSE', 'appsettings.json' }).Count) {
	throw 'Unexpected loose files in release root.'
}

if (Get-ChildItem -LiteralPath $publish -Directory | Where-Object { $_.Name -ne 'python' }) {
	throw 'Unexpected release directory.'
}

$zip = Join-Path $artifacts ($packageName + '.zip')
Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip -Force
Write-Host "Release: $zip"
