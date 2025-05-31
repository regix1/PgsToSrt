#!/bin/bash

# Local publish script
set -e

VERSION="${1:-1.4.6}"
PROJECT_PATH="PgsToSrt/PgsToSrt.csproj"
OUTPUT_DIR="out"

echo "Publishing PgsToSrt version $VERSION"

# Clean previous builds
rm -rf "$OUTPUT_DIR"
mkdir -p "$OUTPUT_DIR"

# Build for each platform
PLATFORMS=("win-x64" "linux-x64" "osx-x64" "osx-arm64")

for PLATFORM in "${PLATFORMS[@]}"; do
    echo "Building for $PLATFORM..."
    
    dotnet publish "$PROJECT_PATH" \
        --configuration Release \
        --runtime "$PLATFORM" \
        --self-contained true \
        --output "$OUTPUT_DIR/$PLATFORM" \
        --verbosity minimal \
        -p:Version="$VERSION" \
        -p:AssemblyVersion="$VERSION.0" \
        -p:FileVersion="$VERSION.0" \
        -p:PublishSingleFile=true \
        -p:PublishTrimmed=true \
        -p:TrimMode=partial \
        -p:DebugType=None \
        -p:DebugSymbols=false

    # Create archives
    cd "$OUTPUT_DIR/$PLATFORM"
    if [[ "$PLATFORM" == win-* ]]; then
        zip -r "../PgsToSrt-$VERSION-$PLATFORM.zip" .
    else
        tar -czf "../PgsToSrt-$VERSION-$PLATFORM.tar.gz" .
    fi
    cd ../..
    
    echo "✓ Created archive for $PLATFORM"
done

# Create checksums
cd "$OUTPUT_DIR"
sha256sum PgsToSrt-$VERSION-*.* > "checksums-$VERSION.txt"
cd ..

echo ""
echo "Build completed! Files created in $OUTPUT_DIR/:"
ls -la "$OUTPUT_DIR"/PgsToSrt-$VERSION-*.*
echo ""
echo "Checksums:"
cat "$OUTPUT_DIR/checksums-$VERSION.txt"