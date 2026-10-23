$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
dotnet build "$projectRoot/src/CFGBINManager/CFGBINManager.csproj" -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Library build failed.' }
dotnet pack "$projectRoot/src/CFGBINManager/CFGBINManager.csproj" -c Release --no-build --output "$projectRoot/dist" --nologo
if ($LASTEXITCODE -ne 0) { throw 'Package creation failed.' }
