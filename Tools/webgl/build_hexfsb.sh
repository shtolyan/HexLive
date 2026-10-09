#!/bin/sh
# §168.6: build hexfsb against a local copy of the FMOD Engine SDK (macOS).
#
#   Tools/webgl/build_hexfsb.sh ~/hex-girls/webgl-build/fmod-2.03.15
#
# The copy holds inc/fsbank*.h and lib/{libfsbank,libfmod,libfsbvorbis,libopus}.dylib
# taken from "FMOD Programmers API/api/{fsbank,core}" of the SDK dmg. The SDK is
# licensed per user and is not committed; the binary lands in <copy>/bin and
# finds its libraries through @executable_path/../lib.
set -eu
sdk="${1:?usage: build_hexfsb.sh <fmod-sdk-copy>}"
here="$(cd "$(dirname "$0")" && pwd)"
mkdir -p "$sdk/bin"
clang -O2 -Wall -Wextra -o "$sdk/bin/hexfsb" "$here/hexfsb.c" \
  -I"$sdk/inc" -L"$sdk/lib" -lfsbank -lfmod \
  -Wl,-rpath,@executable_path/../lib
echo "built $sdk/bin/hexfsb"
