#!/usr/bin/env bash
# Authenticode-signs Windows binaries through SignPath (free OV signing for open-source projects).
# Usage: sign-windows-binaries.sh <file> [<file> ...]  - every file is replaced by its signed version.
#
# Runs inside the Linux CI jobs, so the signature is applied where the artifacts are produced: the
# payload executables before Inno packs them, and the resulting setup .exe before it is uploaded.
#
# NO-OP WITHOUT CREDENTIALS: with SIGNPATH_API_TOKEN unset the script leaves the files untouched and
# exits 0, so ordinary pipelines keep producing the same unsigned builds as before. Keep the CI
# variables *protected* in GitLab - then only tag/main pipelines can sign, and merge-request runs
# never wait on the signing service. Once a token IS present, every failure is fatal: quietly
# shipping an unsigned binary from a pipeline that was told to sign would be the worse outcome.
#
# Required once configured:
#   SIGNPATH_API_TOKEN                    API token of the CI user (protected + masked)
#   SIGNPATH_ORGANIZATION_ID              GUID of the SignPath organization
#   SIGNPATH_PROJECT_SLUG                 project in SignPath (e.g. "linfan")
#   SIGNPATH_SIGNING_POLICY_SLUG          e.g. "release-signing" or "test-signing"
# Optional:
#   SIGNPATH_ARTIFACT_CONFIGURATION_SLUG  artifact configuration; SignPath's default is used if unset
#   SIGNPATH_DESCRIPTION                  shown in SignPath; defaults to the tag/commit
#   SIGNPATH_TIMEOUT_SECONDS              how long to wait for the request (default 900)
#
# API: https://docs.signpath.io/build-system-integration - submit, poll, download the signed artifact.

set -euo pipefail

API_BASE="https://app.signpath.io/API/v1"
POLL_SECONDS=10

if [ "$#" -eq 0 ]; then
    echo "usage: $(basename "$0") <file> [<file> ...]" >&2
    exit 2
fi

if [ -z "${SIGNPATH_API_TOKEN:-}" ]; then
    echo "SignPath: SIGNPATH_API_TOKEN not set - leaving $# artifact(s) unsigned."
    exit 0
fi

for var in SIGNPATH_ORGANIZATION_ID SIGNPATH_PROJECT_SLUG SIGNPATH_SIGNING_POLICY_SLUG; do
    if [ -z "${!var:-}" ]; then
        echo "SignPath: $var is missing although a token is set - refusing to ship unsigned." >&2
        exit 1
    fi
done

if ! command -v jq >/dev/null 2>&1; then
    echo "SignPath: jq is required to read the signing request status." >&2
    exit 1
fi

timeout_seconds="${SIGNPATH_TIMEOUT_SECONDS:-900}"
description="${SIGNPATH_DESCRIPTION:-${CI_COMMIT_TAG:-${CI_COMMIT_SHORT_SHA:-manual build}}}"

sign_one() {
    local file="$1"
    local -a form=(
        -F "ProjectSlug=${SIGNPATH_PROJECT_SLUG}"
        -F "SigningPolicySlug=${SIGNPATH_SIGNING_POLICY_SLUG}"
        -F "Description=${description}"
        -F "Artifact=@${file}"
    )
    if [ -n "${SIGNPATH_ARTIFACT_CONFIGURATION_SLUG:-}" ]; then
        form+=(-F "ArtifactConfigurationSlug=${SIGNPATH_ARTIFACT_CONFIGURATION_SLUG}")
    fi

    echo "==> SignPath: submitting $(basename "$file")"
    # The submission answers 201 with the request URL in the Location header; the body carries nothing
    # we need, so only the headers are kept.
    local location
    location=$(curl -sSf -X POST -D - -o /dev/null \
        -H "Authorization: Bearer ${SIGNPATH_API_TOKEN}" \
        "${form[@]}" \
        "${API_BASE}/${SIGNPATH_ORGANIZATION_ID}/SigningRequests/SubmitWithArtifact" \
        | tr -d '\r' | awk 'tolower($1) == "location:" { print $2 }')

    if [ -z "$location" ]; then
        echo "SignPath: the submission returned no Location header." >&2
        return 1
    fi

    # Poll until the request leaves the pending states. WaitingForApproval is a legitimate wait (a
    # signing policy may require a human), hence the generous default timeout.
    local waited=0 status
    while true; do
        status=$(curl -sSf -H "Authorization: Bearer ${SIGNPATH_API_TOKEN}" "$location" | jq -r '.status')
        case "$status" in
            Completed) break ;;
            InProgress | WaitingForApproval) ;;
            *)
                echo "SignPath: signing request ended as '${status}' - see ${location}" >&2
                return 1
                ;;
        esac
        if [ "$waited" -ge "$timeout_seconds" ]; then
            echo "SignPath: still '${status}' after ${timeout_seconds}s - see ${location}" >&2
            return 1
        fi
        sleep "$POLL_SECONDS"
        waited=$((waited + POLL_SECONDS))
    done

    # Download beside the original and move into place only afterwards, so a failed transfer can never
    # leave a truncated executable behind for the installer to pack.
    curl -sSf -H "Authorization: Bearer ${SIGNPATH_API_TOKEN}" \
        -o "${file}.signed" "${location}/SignedArtifact"
    mv "${file}.signed" "$file"
    echo "==> SignPath: signed $(basename "$file")"
}

for file in "$@"; do
    if [ ! -f "$file" ]; then
        echo "SignPath: $file does not exist." >&2
        exit 1
    fi
    sign_one "$file"
done
