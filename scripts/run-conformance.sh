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
#   SPEC_REF        branch/tag/sha to check out (default: require-sessionid-on-wire)
#
# NOTE: SPEC_REF defaults to the `require-sessionid-on-wire` branch (avohq spec PR #2),
# which fixes the spec to REQUIRE sessionId on the wire — the live ingestion pipeline
# drops events that omit it. Until that PR merges, running against `main` reports
# 20/30 because main still (incorrectly) forbids sessionId. After it merges, set
# SPEC_REF=main.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SPEC_REPO_URL="${SPEC_REPO_URL:-https://github.com/avohq/spec-first-inspector-server-sdk.git}"
SPEC_DIR="${SPEC_DIR:-$ROOT/.spec-repo}"
SPEC_REF="${SPEC_REF:-require-sessionid-on-wire}"
HARNESS_PROJECT="$ROOT/conformance/AvoInspector.Conformance/AvoInspector.Conformance.csproj"
HARNESS_DLL="$ROOT/conformance/AvoInspector.Conformance/bin/Release/net8.0/AvoInspector.Conformance.dll"

echo "==> Building conformance harness"
dotnet build "$HARNESS_PROJECT" -c Release

echo "==> Fetching spec repo (suite-runner + mock server) @ $SPEC_REF"
if [ -d "$SPEC_DIR/.git" ]; then
  git -C "$SPEC_DIR" fetch origin --quiet || true
else
  git clone "$SPEC_REPO_URL" "$SPEC_DIR"
fi
git -C "$SPEC_DIR" checkout "$SPEC_REF" --quiet
git -C "$SPEC_DIR" pull --ff-only origin "$SPEC_REF" --quiet 2>/dev/null || true

echo "==> Running conformance suite"
node "$SPEC_DIR/conformance/runner/suite-runner.mjs" --harness "dotnet $HARNESS_DLL"
