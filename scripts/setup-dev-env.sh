#!/usr/bin/env bash
# Instala .NET SDK 10 e Android SDK (Linux). iOS exige macOS + Xcode (não instalável aqui).
set -euo pipefail
DOTNET_DIR="${DOTNET_DIR:-/opt/dotnet}"
ANDROID_HOME="${ANDROID_HOME:-/opt/android}"

if ! "$DOTNET_DIR/dotnet" --version >/dev/null 2>&1; then
  curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
  bash /tmp/dotnet-install.sh --channel 10.0 --install-dir "$DOTNET_DIR"
fi
ln -sf "$DOTNET_DIR/dotnet" /usr/local/bin/dotnet

if [ ! -x "$ANDROID_HOME/cmdline-tools/latest/bin/sdkmanager" ]; then
  mkdir -p "$ANDROID_HOME/cmdline-tools"
  curl -sSL https://dl.google.com/android/repository/commandlinetools-linux-11076708_latest.zip -o /tmp/cmdtools.zip
  unzip -q -o /tmp/cmdtools.zip -d "$ANDROID_HOME/cmdline-tools"
  mv "$ANDROID_HOME/cmdline-tools/cmdline-tools" "$ANDROID_HOME/cmdline-tools/latest"
fi
yes | "$ANDROID_HOME/cmdline-tools/latest/bin/sdkmanager" --licenses >/dev/null 2>&1 || true
"$ANDROID_HOME/cmdline-tools/latest/bin/sdkmanager" "platform-tools" "platforms;android-35" "build-tools;35.0.0"
echo "Use: export ANDROID_HOME=$ANDROID_HOME"
