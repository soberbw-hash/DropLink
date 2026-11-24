$ErrorActionPreference = 'Stop'
dotnet build "$PSScriptRoot/../src/DropLink/DropLink.csproj" -c Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
