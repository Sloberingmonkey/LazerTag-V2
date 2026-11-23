#!/usr/bin/env bash
# Called by release-readiness-test.py with two running local releases.
set -Eeuo pipefail
repo=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
source "${repo}/deployment/deploy.sh"
work=$(cd -- "$1" && pwd)
good_port=$2
bad_port=$3
RELEASE_ROOT="${work}/releases"
CURRENT_LINK="${work}/current"
NEXT_LINK="${work}/.next"
OLD_RELEASE="${RELEASE_ROOT}/release-good"
RELEASE_ID=release-candidate
RELEASE_DIR="${RELEASE_ROOT}/${RELEASE_ID}"

# Use the same interpreter that launched the fixture on either host OS.
python3() { "${TEST_PYTHON}" "$@"; }
validate_release_files "${OLD_RELEASE}"
activate_release "${OLD_RELEASE}"
if (validate_release_files "${RELEASE_DIR}"); then exit 1; fi
cp "${OLD_RELEASE}/wwwroot/tv.html" "${RELEASE_DIR}/wwwroot/tv.html"
cp "${RELEASE_DIR}/GameTypes.xml" "${work}/catalog.xml"
for invalid in missing malformed empty-catalog; do
    case "${invalid}" in
        missing)      rm -f -- "${RELEASE_DIR}/GameTypes.xml" ;;
        malformed)    printf '<' > "${RELEASE_DIR}/GameTypes.xml" ;;
        empty-catalog) printf '<LazerSwarm/>' > "${RELEASE_DIR}/GameTypes.xml" ;;
    esac
    if (validate_release_files "${RELEASE_DIR}"); then exit 1; fi
    [[ $(readlink -f "${CURRENT_LINK}") == "${OLD_RELEASE}" ]]
done
cp "${work}/catalog.xml" "${RELEASE_DIR}/GameTypes.xml"
validate_release_files "${RELEASE_DIR}"

# The candidate process saw the incomplete release at startup. Substitute
# only service restarts and the wait interval. Exercise real link swaps
# and the production health checker against each process's /healthz.
systemctl() {
    [[ $1 == restart && $2 == "${SERVICE}" ]] || return 1
    readlink "${CURRENT_LINK}" >> "${work}/restarts"
}
probe_active() {
    local port=${bad_port}
    if [[ $(readlink -f "${CURRENT_LINK}") == "${OLD_RELEASE}" ]]; then port=${good_port}; fi
    bash "${repo}/deployment/health-check.sh" --url "http://127.0.0.1:${port}/healthz" --wait 0 --quiet
}
HEALTH_CHECK=probe_active
if activate_and_verify; then exit 1; fi
[[ $(readlink -f "${CURRENT_LINK}") == "${OLD_RELEASE}" ]]
mapfile -t restarts < "${work}/restarts"
[[ ${#restarts[@]} == 2 && ${restarts[0]} == releases/release-candidate && ${restarts[1]} == releases/release-good ]]
probe_active
