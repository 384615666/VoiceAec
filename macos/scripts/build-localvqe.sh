#!/usr/bin/env bash
# Build a self-contained universal (x86_64 + arm64) liblocalvqe.dylib.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
MACOS="$ROOT/macos"
SRC="${LOCALVQE_SRC:-$ROOT/.build/localvqe-macos}"
COMMIT="7f61cd0"
PATCH="$ROOT/native/LocalVQE-macos.patch"

find_tool() {
  local name="$1"
  if [ -x "$ROOT/.tools/py/bin/$name" ]; then
    export PYTHONPATH="$ROOT/.tools/py${PYTHONPATH:+:$PYTHONPATH}"
    echo "$ROOT/.tools/py/bin/$name"
  elif command -v "$name" >/dev/null 2>&1; then
    command -v "$name"
  else
    echo ""
  fi
}
CMAKE="$(find_tool cmake)"
NINJA="$(find_tool ninja)"
[ -n "$CMAKE" ] || { echo "error: cmake not found (brew install cmake ninja)" >&2; exit 1; }
[ -n "$NINJA" ] || { echo "error: ninja not found (brew install cmake ninja)" >&2; exit 1; }

if [ ! -d "$SRC/.git" ]; then
  echo "==> cloning LocalVQE into $SRC"
  mkdir -p "$(dirname "$SRC")"
  git clone --recursive https://github.com/localai-org/LocalVQE.git "$SRC"
fi
echo "==> checking out $COMMIT and applying macOS patch"
git -C "$SRC" fetch --tags --quiet origin || true
git -C "$SRC" checkout -f "$COMMIT"
git -C "$SRC" submodule update --init --recursive
git -C "$SRC" checkout -- ggml/CMakeLists.txt
git -C "$SRC" submodule foreach --recursive 'git checkout -f -- .' >/dev/null
git -C "$SRC" apply "$PATCH"

build_arch() {
  local arch="$1" build="$2" extra=("${@:3}")
  echo "==> configuring $arch"
  "$CMAKE" -S "$SRC/ggml" -B "$SRC/$build" -G Ninja \
    -DCMAKE_MAKE_PROGRAM="$NINJA" \
    -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_OSX_DEPLOYMENT_TARGET=13.0 \
    -DLOCALVQE_BUILD_SHARED=ON \
    -DLOCALVQE_PORTABLE=ON \
    "${extra[@]}"
  echo "==> building $arch"
  "$CMAKE" --build "$SRC/$build" --target localvqe_shared --parallel "$(sysctl -n hw.ncpu)"
}

build_arch x86_64 build-mac-x64 \
  -DCMAKE_OSX_ARCHITECTURES=x86_64 \
  -DCMAKE_C_FLAGS="-march=haswell" \
  -DCMAKE_CXX_FLAGS="-march=haswell"

cat > "$SRC/arm64-toolchain.cmake" <<'TOOLCHAIN'
set(CMAKE_SYSTEM_NAME Darwin)
set(CMAKE_SYSTEM_PROCESSOR arm64)
set(CMAKE_OSX_ARCHITECTURES arm64 CACHE STRING "" FORCE)
set(CMAKE_OSX_DEPLOYMENT_TARGET 13.0 CACHE STRING "" FORCE)
set(CMAKE_C_COMPILER /usr/bin/clang)
set(CMAKE_CXX_COMPILER /usr/bin/clang++)
TOOLCHAIN

build_arch arm64 build-mac-arm64 \
  -DCMAKE_TOOLCHAIN_FILE="$SRC/arm64-toolchain.cmake" \
  -DCMAKE_OSX_ARCHITECTURES=arm64

mkdir -p "$MACOS/Vendor"
OUT="$MACOS/Vendor/liblocalvqe.dylib"
lipo -create "$SRC/build-mac-x64/liblocalvqe.0.1.0.dylib" \
            "$SRC/build-mac-arm64/liblocalvqe.0.1.0.dylib" -output "$OUT"
install_name_tool -id @rpath/liblocalvqe.dylib "$OUT"
lipo -info "$OUT"
echo "==> wrote $OUT"
otool -L "$OUT" | head -8
