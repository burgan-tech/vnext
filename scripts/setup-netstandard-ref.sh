#!/bin/bash
# Setup script for NETStandard.Library.Ref for PostSharp on .NET 10

echo "Installing NETStandard.Library.Ref 2.1.0 for PostSharp compatibility..."

# Download the package
wget -q https://www.nuget.org/api/v2/package/NETStandard.Library.Ref/2.1.0 -O /tmp/netstandard.nupkg

# Install to NuGet cache
NUGET_CACHE="$HOME/.nuget/packages/netstandard.library.ref/2.1.0"
mkdir -p "$NUGET_CACHE"
unzip -q /tmp/netstandard.nupkg -d "$NUGET_CACHE"
rm /tmp/netstandard.nupkg

echo "✅ NETStandard.Library.Ref installed successfully!"
echo "You can now run: dotnet build"
