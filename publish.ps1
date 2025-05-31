# Local publish script for Windows
param(
    [string]$Version = "1.4.6"
)

$ErrorActionPreference = "Stop"

$ProjectPath = "PgsToSrt/PgsToSrt.csproj"
$OutputDir = "out"

Write-Host "Publishing PgsToSrt version $Version" -ForegroundColor Green

# Clean previous builds
if (Test-Path $OutputDir) {
    Remove-Item $OutputDir -Recurse -Force
}
New-Item -ItemType Directory -Path $OutputDir | Out-Null

# Build for each platform
$Platforms = @("win-x64", "linux-x64", "osx-x64", "osx-arm64")

foreach ($Platform in $Platforms) {
    Write-Host "Building for $Platform..." -ForegroundColor Yellow
    
    dotnet publish $ProjectPath `
        --configuration Release `
        --runtime $Platform `
        --self-contained true `
        --output "$OutputDir/$Platform" `
        --verbosity minimal `
        -p:Version=$Version `
        -p:AssemblyVersion="$Version.0" `
        -p:FileVersion="$Version.0" `
        -p:PublishSingleFile=true `
        -p:PublishTrimmed=true `
        -p:TrimMode=partial `
        -p:DebugType=None `
        -p:DebugSymbols=false

    # Create archives
    Push-Location "$OutputDir/$Platform"
    if ($Platform -like "win-*") {
        Compress-Archive -Path "." -DestinationPath "../PgsToSrt-$Version-$Platform.zip" -Force
    } else {
        tar -czf "../PgsToSrt-$Version-$Platform.tar.gz" .
    }
    Pop-Location
    
    Write-Host "✓ Created archive for $Platform" -ForegroundColor Green
}

# Create checksums
Push-Location $OutputDir
Get-ChildItem "PgsToSrt-$Version-*.*" | Get-FileHash -Algorithm SHA256 | 
    ForEach-Object { "$($_.Hash.ToLower())  $($_.Name)" } | 
    Out-File "checksums-$Version.txt" -Encoding UTF8
Pop-Location

Write-Host ""
Write-Host "Build completed! Files created in $OutputDir/:" -ForegroundColor Green
Get-ChildItem "$OutputDir/PgsToSrt-$Version-*.*" | Format-Table Name, Length
Write-Host ""
Write-Host "Checksums:" -ForegroundColor Green
Get-Content "$OutputDir/checksums-$Version.txt"