#!/usr/bin/env bash
set -Eeuo pipefail

URL=${LAZERTAG_HEALTH_URL:-http://127.0.0.1:5000/healthz}
WAIT_SECONDS=0
QUIET=0

usage() {
    cat <<'EOF'
Usage: health-check [--url URL] [--wait SECONDS] [--quiet]

Returns success only when the unauthenticated /healthz endpoint returns 2xx.
EOF
}

die() {
    printf 'health-check: %s\n' "$*" >&2
    exit 2
}

while (($#)); do
    case "$1" in
        --url)
            (($# >= 2)) || die '--url requires a value'
            URL=$2
            shift 2
            ;;
        --wait)
            (($# >= 2)) || die '--wait requires seconds'
            WAIT_SECONDS=$2
            shift 2
            ;;
        --quiet)
            QUIET=1
            shift
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            usage >&2
            die "unknown argument: $1"
            ;;
    esac
done

[[ ${WAIT_SECONDS} =~ ^[0-9]+$ ]] || die '--wait must be a non-negative integer'
((WAIT_SECONDS <= 300)) || die '--wait cannot exceed 300 seconds'
command -v curl >/dev/null || die 'curl is required'

DEADLINE=$((SECONDS + WAIT_SECONDS))
LAST_ERROR='no response'

while :; do
    if RESPONSE=$(curl \
        --fail \
        --silent \
        --show-error \
        --connect-timeout 2 \
        --max-time 4 \
        --header 'Accept: application/json' \
        "${URL}" 2>&1); then
        if ((QUIET == 0)); then
            printf '%s\n' "${RESPONSE}"
        fi
        exit 0
    else
        LAST_ERROR=${RESPONSE}
    fi

    ((SECONDS < DEADLINE)) || break
    sleep 1
done

if ((QUIET == 0)); then
    printf 'Health check failed for %s: %s\n' "${URL}" "${LAST_ERROR}" >&2
fi
exit 1
