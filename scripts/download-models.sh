#!/usr/bin/env bash
# Downloads the ONNX embedding model + tokenizer files (SRS D9, D30) into models/multilingual-e5-small/.
# Files come from a pinned Hugging Face commit and are verified against SHA-256 hashes below.
# Idempotent: a file that is already present with the right hash is not downloaded again.
# Usage: scripts/download-models.sh [target-dir]
set -euo pipefail

REPO="Xenova/multilingual-e5-small"
REVISION="761b726dd34fb83930e26aab4e9ac3899aa1fa78"   # pinned commit; changing it changes the embeddings (SRS C3)

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TARGET="${1:-$ROOT/models/multilingual-e5-small}"
mkdir -p "$TARGET"

# <path in repo>|<local file name>|<sha256>
FILES=(
  "onnx/model_quantized.onnx|model_quantized.onnx|f80102d3f2a1229f387d3c81909990d8945513e347b0eab049f7de3c6f98c193"
  "sentencepiece.bpe.model|sentencepiece.bpe.model|cfc8146abe2a0488e9e2a0c56de7952f7c11ab059eca145a0a727afce0db2865"
  "tokenizer.json|tokenizer.json|0b44a9d7b51c3c62626640cda0e2c2f70fdacdc25bbbd68038369d14ebdf4c39"
)

sha256_of() { sha256sum "$1" | cut -d' ' -f1; }

for entry in "${FILES[@]}"; do
  IFS='|' read -r remote local expected <<<"$entry"
  dest="$TARGET/$local"
  if [[ -f "$dest" && "$(sha256_of "$dest")" == "$expected" ]]; then
    echo "ok       $local"
    continue
  fi
  echo "download $local"
  tmp="$dest.partial"
  curl --fail --silent --show-error --location --retry 3 \
    -o "$tmp" "https://huggingface.co/$REPO/resolve/$REVISION/$remote"
  actual="$(sha256_of "$tmp")"
  if [[ "$actual" != "$expected" ]]; then
    rm -f "$tmp"
    echo "ERROR: SHA-256 mismatch for $local (expected $expected, got $actual)" >&2
    exit 1
  fi
  mv "$tmp" "$dest"
done
echo "Model files are ready in $TARGET"
