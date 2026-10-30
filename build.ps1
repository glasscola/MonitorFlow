$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'MonitorFlow/MonitorFlow.csproj'
$destination = Join-Path $PSScriptRoot 'release'
dotnet restore $project -r win-x64 -p:SelfContained=true --source https://api.nuget.org/v3/index.json
if ($LASTEXITCODE -ne 0) { throw 'Restore failed' }
dotnet publish $project -c Release -r win-x64 --self-contained true --no-restore -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -o $destination
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
Write-Output "Built: $destination/MonitorFlow.exe"
