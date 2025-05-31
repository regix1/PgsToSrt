#!/bin/bash

# Local publish script for Linux
set -e

VERSION="${1:-1.4.6}"
OUTPUT_DIR="out"

echo "Publishing PgsToSrt version $VERSION for Linux"

# Find project file
if [ -f "PgsToSrt/PgsToSrt.csproj" ]; then
    PROJECT_PATH="PgsToSrt/PgsToSrt.csproj"
elif [ -f "src/PgsToSrt/PgsToSrt.csproj" ]; then
    PROJECT_PATH="src/PgsToSrt/PgsToSrt.csproj"
else
    echo "❌ Could not find PgsToSrt.csproj"
    echo "Available .csproj files:"
    find . -name "*.csproj" -type f
    exit 1
fi

echo "Using project: $PROJECT_PATH"

# Clean previous builds
rm -rf "$OUTPUT_DIR"
mkdir -p "$OUTPUT_DIR"

echo "Building for linux-x64..."

dotnet publish "$PROJECT_PATH" \
    --configuration Release \
    --runtime linux-x64 \
    --self-contained true \
    --output "$OUTPUT_DIR/linux-x64" \
    --verbosity minimal \
    -p:Version="$VERSION" \
    -p:AssemblyVersion="$VERSION.0" \
    -p:FileVersion="$VERSION.0" \
    -p:PublishSingleFile=true \
    -p:DebugType=None \
    -p:DebugSymbols=false

# Create archive
cd "$OUTPUT_DIR/linux-x64"
tar -czf "../PgsToSrt-$VERSION-linux-x64.tar.gz" .
cd ../..

# Create checksums
cd "$OUTPUT_DIR"
sha256sum "PgsToSrt-$VERSION-linux-x64.tar.gz" > "checksums-$VERSION.txt"
cd ..

echo ""
echo "✓ Build completed! Files created in $OUTPUT_DIR/:"
ls -la "$OUTPUT_DIR"/PgsToSrt-$VERSION-*.*
echo ""
echo "Checksums:"
cat "$OUTPUT_DIR/checksums-$VERSION.txt"