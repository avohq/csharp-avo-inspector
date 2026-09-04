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
#   SPEC_REF        branch/tag/sha to check out (default: main)
#
# `main` is spec 2.0.0 (30 fixtures). Spec 2.1.0 — the gateway track options this SDK
# implements (SPEC.md §4.2.1/§7.3.6) — is still open as avohq spec PR #3, so to exercise
# its six extra fixtures (wire-9…wire-13, batch-7) run:
#
#   SPEC_REF=gateway-track-options ./scripts/run-conformance.sh   # 36/36
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SPEC_REPO_URL="${SPEC_REPO_URL:-https://github.com/avohq/spec-first-inspector-server-sdk.git}"
SPEC_DIR="${SPEC_DIR:-$ROOT/.spec-repo}"
SPEC_REF="${SPEC_REF:-main}"
HARNESS_PROJECT="$ROOT/conformance/AvoInspector.Conformance/AvoInspector.Conformance.csproj"
HARNESS_DLL="$ROOT/conformance/AvoInspector.Conformance/bin/Release/net8.0/AvoInspector.Conformance.dll"

echo "==> Building conformance harness"
dotnet build "$HARNESS_PROJECT" -c Release

echo "==> Fetching spec repo (suite-runner + mock server) @ $SPEC_REF"
# Fail-closed + deterministic: fetch the exact ref and hard-checkout FETCH_HEAD. With `set -e`
# a fetch/checkout failure aborts the run (no silent fallback to a stale/drifted .spec-repo), and
# --force discards any local drift so every run reflects exactly the remote $SPEC_REF.
if [ ! -d "$SPEC_DIR/.git" ]; then
  git clone --quiet "$SPEC_REPO_URL" "$SPEC_DIR"
fi
git -C "$SPEC_DIR" fetch --quiet origin "$SPEC_REF"
git -C "$SPEC_DIR" -c advice.detachedHead=false checkout --quiet --force FETCH_HEAD
echo "    spec @ $(git -C "$SPEC_DIR" rev-parse --short HEAD) ($SPEC_REF)"

echo "==> Running conformance suite"
node "$SPEC_DIR/conformance/runner/suite-runner.mjs" --harness "dotnet $HARNESS_DLL"
