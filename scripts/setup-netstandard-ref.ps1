# Setup script for NETStandard.Library.Ref for PostSharp on .NET 10

Write-Host "Installing NETStandard.Library.Ref 2.1.0 for PostSharp compatibility..." -ForegroundColor Green

# Download the package
$tempFile = "$env:TEMP\netstandard.nupkg"
Invoke-WebRequest -Uri "https://www.nuget.org/api/v2/package/NETStandard.Library.Ref/2.1.0" -OutFile $tempFile

# Install to NuGet cache
$nugetCache = "$env:USERPROFILE\.nuget\packages\netstandard.library.ref\2.1.0"
New-Item -ItemType Directory -Force -Path $nugetCache | Out-Null
Expand-Archive -Path $tempFile -DestinationPath $nugetCache -Force
Remove-Item $tempFile

Write-Host "✅ NETStandard.Library.Ref installed successfully!" -ForegroundColor Green
Write-Host "You can now run: dotnet build" -ForegroundColor Yellow
