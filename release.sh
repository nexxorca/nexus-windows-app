#!/bin/bash
# Usage: ./release.sh 1.0.1
set -e

VERSION=$1
NEXUS_URL="https://nexus.nexxor.ca"
RELEASE_SECRET="498df6e8f73fd5babd7330aeb570acb249c06430023e6f1dda5a7cf2378af348"

if [ -z "$VERSION" ]; then
    echo "Usage: ./release.sh <version>"
    exit 1
fi

if [ -f "./Releases/NexusApp-$VERSION-full.nupkg" ]; then
    echo "Error: Version $VERSION already exists locally. Bump the version number."
    exit 1
fi

echo "Building version $VERSION..."
sed -i "s|<Version>.*</Version>|<Version>$VERSION</Version>|" src/Nexus.App/Nexus.App.csproj
dotnet publish src/Nexus.App/Nexus.App.csproj -c Release -r win-x64 --self-contained -o publish

echo "Packing with vpk..."
rm -f ./Releases/NexusApp-$VERSION-*.nupkg ./Releases/NexusApp-$VERSION-*.exe
vpk pack -u NexusApp -v $VERSION -p ./publish -e Nexus.App.exe --packTitle "Nexus" --packAuthors "NEXXOR inc." --icon src/Nexus.App/Resources/nexus.ico

NUPKG="./Releases/NexusApp-$VERSION-full.nupkg"
FILE_SIZE=$(ls -lh "$NUPKG" | awk '{print $5}')
echo "Uploading $FILE_SIZE to Nexus (this may take a minute)..."
curl --fail -X POST "$NEXUS_URL/api/v1/app/releases" \
  -H "Authorization: Bearer $RELEASE_SECRET" \
  -F "version=$VERSION" \
  -F "file=@$NUPKG"
echo "Upload complete."

echo "Uploading release manifests..."
curl --fail -X POST "$NEXUS_URL/api/v1/app/releases/manifest" \
  -H "Authorization: Bearer $RELEASE_SECRET" \
  -F "file=@./Releases/RELEASES"

curl --fail -X POST "$NEXUS_URL/api/v1/app/releases/manifest" \
  -H "Authorization: Bearer $RELEASE_SECRET" \
  -F "file=@./Releases/releases.win.json"

DELTA="./Releases/NexusApp-$VERSION-delta.nupkg"
if [ -f "$DELTA" ]; then
    echo "Uploading delta package..."
    curl --fail -X POST "$NEXUS_URL/api/v1/app/releases/manifest" \
      -H "Authorization: Bearer $RELEASE_SECRET" \
      -F "file=@$DELTA"
fi

echo "Done. Version $VERSION released."
