[CmdletBinding()]
param(
	[ValidateSet("x64", "x86", "arm64")]
	[string]$Architecture = "x64"
)

$ErrorActionPreference = "Stop"
$profile = "portable-win-$Architecture"

Write-Host "Publishing Clay as a self-contained portable build for win-$Architecture..."
dotnet publish .\Clay.csproj `
	--configuration Release `
	--runtime "win-$Architecture" `
	--self-contained true `
	-p:PublishProfile=$profile

$output = Join-Path $PSScriptRoot "publish\win-$Architecture"
Write-Host "Published to $output"
Write-Host "Run $output\Clay.exe with the complete folder beside it."
