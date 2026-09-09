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
# `main` carries spec 3.0.0 (36 fixtures) — the unified POST /inspector/v2/track endpoint and its
# REQUIRED api-key / env / X-Avo-Client request headers (SPEC.md §7.1/§7.2), the gateway track
# options once drafted as 2.1.0 (SPEC.md §4.2.1/§7.3.6), and the removal of the wire sessionId
# (SPEC.md §3.3). That is the version this SDK records in InspectorVersion.SpecVersion, so the
# default ref is the one that produces a clean run.
#
# 3.0.0 reached `main` in avohq/spec-first-inspector-server-sdk#3 (merged 2026-09-09), which is why
# the default is no longer the `gateway-track-options` branch that PR was developed on. The branch
# still exists and is tree-identical to the merge, so an older `SPEC_REF=gateway-track-options`
# invocation keeps working; prefer `main`. The spec repo publishes no version tags, so there is no
# `v3.0.0` ref to pin to instead.
#
# No harness change is needed for the headers: the suite drives the SDK through
# AVO_INSPECTOR_MOCK_ENDPOINT, and the runner records request headers itself and asserts them
# via a fixture's expected_request_headers — wire-1 and batch-1 pin all three.
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
