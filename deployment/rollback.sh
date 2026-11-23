#!/usr/bin/env bash
set -Eeuo pipefail

SERVICE=lazertag.service
RELEASE_ROOT=/opt/lazertag/releases
CURRENT_LINK=/opt/lazertag/current
TARGET_ARG=previous

usage() {
    cat <<'EOF'
Usage: sudo lazertag-rollback [previous|release-HEX]
       sudo lazertag-rollback --list

Switches to an already-installed release and verifies /healthz. Persistent data
is backed up but is not rewound automatically.
EOF
}

die() {
    printf 'lazertag-rollback: %s\n' "$*" >&2
    exit 1
}

[[ ${EUID} -eq 0 ]] || die 'must run as root'

if (($#)); then
    case "$1" in
        --list)
            CURRENT=''
            [[ -L ${CURRENT_LINK} ]] && CURRENT=$(readlink -f -- "${CURRENT_LINK}" || true)
            while IFS= read -r RELEASE; do
                if [[ ${RELEASE} == "${CURRENT}" ]]; then
                    printf '* %s\n' "$(basename -- "${RELEASE}")"
                else
                    printf '  %s\n' "$(basename -- "${RELEASE}")"
                fi
            done < <(find "${RELEASE_ROOT}" -mindepth 1 -maxdepth 1 -type d -name 'release-*' -printf '%T@ %p\n' | sort -nr | cut -d' ' -f2-)
            exit 0
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            TARGET_ARG=$1
            shift
            ;;
    esac
fi
(($# == 0)) || { usage >&2; exit 2; }

for COMMAND in flock systemctl; do
    command -v "${COMMAND}" >/dev/null || die "required command not found: ${COMMAND}"
done
[[ -L ${CURRENT_LINK} ]] || die 'there is no active release'

exec 9>>/run/lock/lazertag-deploy.lock
flock -n 9 || die 'a game, pending results save, or another update is active'

# The shell helper installed beside this script also handles older servers
# which need a one-time offline cutover.
( source /usr/local/sbin/lazertag-deploy; check_running_arena_guard )

CURRENT_RELEASE=$(readlink -f -- "${CURRENT_LINK}")
[[ ${CURRENT_RELEASE} == "${RELEASE_ROOT}"/* && -d ${CURRENT_RELEASE} ]] || die 'current release link is invalid'

if [[ ${TARGET_ARG} == previous ]]; then
    TARGET_RELEASE=''
    while IFS= read -r CANDIDATE; do
        if [[ ${CANDIDATE} != "${CURRENT_RELEASE}" ]]; then
            TARGET_RELEASE=${CANDIDATE}
            break
        fi
    done < <(find "${RELEASE_ROOT}" -mindepth 1 -maxdepth 1 -type d -name 'release-*' -printf '%T@ %p\n' | sort -nr | cut -d' ' -f2-)
    [[ -n ${TARGET_RELEASE} ]] || die 'no previous release is installed'
else
    [[ ${TARGET_ARG} =~ ^release-[0-9a-f]{16}$ ]] || die 'release id must look like release-0123456789abcdef'
    TARGET_RELEASE=${RELEASE_ROOT}/${TARGET_ARG}
    [[ -d ${TARGET_RELEASE} && ! -L ${TARGET_RELEASE} ]] || die "release is not installed: ${TARGET_ARG}"
fi

[[ ${TARGET_RELEASE} != "${CURRENT_RELEASE}" ]] || die 'requested release is already active'

NEXT_LINK=/opt/lazertag/.current-$$
cleanup() {
    local status=$?
    trap - EXIT INT TERM
    rm -f -- "${NEXT_LINK}"
    exit "${status}"
}
trap cleanup EXIT INT TERM

activate_release() {
    local target=$1
    local release_name
    release_name=$(basename -- "${target}")
    [[ ${target} == "${RELEASE_ROOT}"/* && -d ${target} ]] || die 'refusing to activate an invalid release path'
    ln -s -- "releases/${release_name}" "${NEXT_LINK}"
    mv -Tf -- "${NEXT_LINK}" "${CURRENT_LINK}"
}

/usr/local/sbin/lazertag-backup >/dev/null
activate_release "${TARGET_RELEASE}"

if systemctl restart "${SERVICE}" \
    && /usr/local/libexec/lazertag/health-check --wait 45 --quiet; then
    printf 'Rolled back from %s to %s.\n' \
        "$(basename -- "${CURRENT_RELEASE}")" "$(basename -- "${TARGET_RELEASE}")"
else
    printf 'Requested rollback failed health validation; restoring %s.\n' "$(basename -- "${CURRENT_RELEASE}")" >&2
    activate_release "${CURRENT_RELEASE}"
    systemctl restart "${SERVICE}" || true
    /usr/local/libexec/lazertag/health-check --wait 45 --quiet || true
    exit 1
fi
