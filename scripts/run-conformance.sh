#!/usr/bin/env bash
#
# Build the C# conformance harness and run the official Avo Inspector conformance
# suite against it. The language-agnostic suite-runner and mock server live in the
# spec repository (avohq/spec-first-inspector-server-sdk); this script fetches it
# (shallow) and points its runner at the built harness.
#
# Usage:
#   ./scripts/run-conformance.sh
#
# Environment overrides:
#   SPEC_REPO_URL   git URL of the spec repo (default: the public avohq repo)
#   SPEC_DIR        local checkout path     (default: <repo>/.spec-repo)
#
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SPEC_REPO_URL="${SPEC_REPO_URL:-https://github.com/avohq/spec-first-inspector-server-sdk.git}"
SPEC_DIR="${SPEC_DIR:-$ROOT/.spec-repo}"
HARNESS_PROJECT="$ROOT/conformance/AvoInspector.Conformance/AvoInspector.Conformance.csproj"
HARNESS_DLL="$ROOT/conformance/AvoInspector.Conformance/bin/Release/net8.0/AvoInspector.Conformance.dll"

echo "==> Building conformance harness"
dotnet build "$HARNESS_PROJECT" -c Release

echo "==> Fetching spec repo (suite-runner + mock server)"
if [ -d "$SPEC_DIR/.git" ]; then
  git -C "$SPEC_DIR" pull --ff-only || true
else
  git clone --depth 1 "$SPEC_REPO_URL" "$SPEC_DIR"
fi

echo "==> Running conformance suite"
node "$SPEC_DIR/conformance/runner/suite-runner.mjs" --harness "dotnet $HARNESS_DLL"
