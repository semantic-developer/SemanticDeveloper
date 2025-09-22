$ErrorActionPreference = 'Stop'
param(
  [string]$Rid = 'win-x64',
  [ValidateSet('Zip','Inno')][string]$Mode = 'Zip'
)

$root = Resolve-Path "$PSScriptRoot/../../"
$appProj = Join-Path $root 'SemanticDeveloper/SemanticDeveloper.csproj'
$outDir = Join-Path $PSScriptRoot 'out/publish'
$artifacts = Join-Path $PSScriptRoot 'artifacts'
New-Item -Force -ItemType Directory -Path $outDir | Out-Null
New-Item -Force -ItemType Directory -Path $artifacts | Out-Null

Write-Host "Publishing SemanticDeveloper for $Rid ..."
dotnet publish $appProj -c Release -r $Rid --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true /p:PublishTrimmed=false -o $outDir

if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

if ($Mode -eq 'Zip') {
  $zip = Join-Path $artifacts "SemanticDeveloper-$Rid.zip"
  if (Test-Path $zip) { Remove-Item $zip -Force }
  Write-Host "Zipping to $zip ..."
  Add-Type -AssemblyName System.IO.Compression.FileSystem
  [System.IO.Compression.ZipFile]::CreateFromDirectory($outDir, $zip)
  Write-Host "Done: $zip"
  exit 0
}

if ($Mode -eq 'Inno') {
  $iscc = (Get-Command iscc.exe -ErrorAction SilentlyContinue)
  if (-not $iscc) { throw 'Inno Setup (iscc.exe) not found in PATH.' }
  $iss = Join-Path $PSScriptRoot 'SemanticDeveloper.iss'
  & $iscc $iss /DRID=$Rid /DOutDir=$outDir /DArtifactsDir=$artifacts
  if ($LASTEXITCODE -ne 0) { throw 'Inno Setup build failed.' }
  Write-Host "Done. See artifacts folder."
  exit 0
}

