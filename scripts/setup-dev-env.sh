#!/usr/bin/env bash
# Instala .NET SDK 10 e Android SDK (Linux). iOS exige macOS + Xcode (não instalável aqui).
# Segurança (SR-017): downloads em diretório temporário privado (mktemp -d), verificação de SHA-256
# antes de executar/extrair, sem licenças aceitas automaticamente.
#
# Checksums fixados abaixo. Ao atualizar uma versão, obtenha o hash de uma fonte independente do download
# (página oficial do fornecedor) e revise o diff do script antes de executar.
#   - commandlinetools-linux-11076708: SHA-256 publicado por Google em developer.android.com/studio#command-line-tools-only
#   - dotnet-install.sh: Microsoft não publica hash; o valor abaixo foi calculado em 2026-10-08 (confiança no primeiro uso).
#     O próprio dotnet-install.sh valida o SDK baixado contra o checksum do feed oficial.
set -euo pipefail
umask 077

DOTNET_DIR="${DOTNET_DIR:-/opt/dotnet}"
ANDROID_HOME="${ANDROID_HOME:-/opt/android}"
LINK_DIR="${LINK_DIR:-/usr/local/bin}"
ACCEPT_ANDROID_LICENSES="${ACCEPT_ANDROID_LICENSES:-}"

DOTNET_INSTALL_SHA256="082f7685e156738a1b2e2ed8381a621870d4ce8e8c59278034556f05c186eb2e"
CMDTOOLS_FILE="commandlinetools-linux-11076708_latest.zip"
CMDTOOLS_SHA256="2d2d50857e4eb553af5a6dc3ad507a17adf43d115264b1afc116f95c92e5e258"

WORK="$(mktemp -d -t nina-setup.XXXXXXXX)"
trap 'rm -rf -- "$WORK"' EXIT

fetch() { # url destino
  curl --proto "=https" --tlsv1.2 -fsSL --retry 3 -o "$2" "$1"
}

verify_sha256() { # arquivo hash-esperado
  local actual
  actual="$(sha256sum -- "$1" | awk '{print $1}')"
  if [ "$actual" != "$2" ]; then
    echo "ERRO: SHA-256 divergente para $(basename -- "$1")" >&2
    echo "  esperado: $2" >&2
    echo "  obtido:   $actual" >&2
    exit 1
  fi
}

if ! "$DOTNET_DIR/dotnet" --version >/dev/null 2>&1; then
  fetch https://dot.net/v1/dotnet-install.sh "$WORK/dotnet-install.sh"
  verify_sha256 "$WORK/dotnet-install.sh" "$DOTNET_INSTALL_SHA256"
  bash "$WORK/dotnet-install.sh" --channel 10.0 --install-dir "$DOTNET_DIR"
fi
ln -sf "$DOTNET_DIR/dotnet" "$LINK_DIR/dotnet"

if [ ! -x "$ANDROID_HOME/cmdline-tools/latest/bin/sdkmanager" ]; then
  fetch "https://dl.google.com/android/repository/$CMDTOOLS_FILE" "$WORK/cmdtools.zip"
  verify_sha256 "$WORK/cmdtools.zip" "$CMDTOOLS_SHA256"
  mkdir -p "$ANDROID_HOME/cmdline-tools" "$WORK/extract"
  unzip -q "$WORK/cmdtools.zip" -d "$WORK/extract"
  rm -rf -- "$ANDROID_HOME/cmdline-tools/latest"
  mv "$WORK/extract/cmdline-tools" "$ANDROID_HOME/cmdline-tools/latest"
fi

SDKMANAGER="$ANDROID_HOME/cmdline-tools/latest/bin/sdkmanager"
if [ "$ACCEPT_ANDROID_LICENSES" = "yes" ]; then
  # Aceite explícito e consciente (variável de ambiente), nunca implícito.
  yes | "$SDKMANAGER" --licenses >/dev/null 2>&1 || true
else
  echo "Licenças do Android SDK NÃO aceitas automaticamente."
  echo "Leia e aceite com: $SDKMANAGER --licenses   (ou reexecute com ACCEPT_ANDROID_LICENSES=yes)"
fi
"$SDKMANAGER" "platform-tools" "platforms;android-35" "build-tools;35.0.0"
echo "Use: export ANDROID_HOME=$ANDROID_HOME"
