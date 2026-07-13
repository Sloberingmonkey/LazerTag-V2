#!/usr/bin/env bash
set -Eeuo pipefail

SERVICE=lazertag.service
RELEASE_ROOT=/opt/lazertag/releases
CURRENT_LINK=/opt/lazertag/current
KEEP_RELEASES=5

usage() {
    cat <<'EOF'
Usage: sudo lazertag-deploy RELEASE.tar.gz [--keep-releases COUNT] [--no-backup]

Installs a flat linux-arm64 self-contained publish archive, atomically switches
/opt/lazertag/current, restarts the service, and rolls back automatically if the
/healthz readiness check does not pass.
EOF
}

die() {
    printf 'lazertag-deploy: %s\n' "$*" >&2
    exit 1
}

[[ ${EUID} -eq 0 ]] || die 'must run as root'
(($# >= 1)) || { usage >&2; exit 2; }

ARCHIVE=$1
shift
MAKE_BACKUP=1

while (($#)); do
    case "$1" in
        --keep-releases)
            (($# >= 2)) || die '--keep-releases requires a count'
            KEEP_RELEASES=$2
            shift 2
            ;;
        --no-backup)
            MAKE_BACKUP=0
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

[[ ${KEEP_RELEASES} =~ ^[0-9]+$ ]] || die '--keep-releases must be an integer'
((KEEP_RELEASES >= 2)) || die '--keep-releases must be at least 2'
((KEEP_RELEASES <= 50)) || die '--keep-releases cannot exceed 50'
[[ -f ${ARCHIVE} && -r ${ARCHIVE} ]] || die "cannot read release archive: ${ARCHIVE}"
ARCHIVE=$(readlink -f -- "${ARCHIVE}")

for COMMAND in flock sha256sum tar file systemctl; do
    command -v "${COMMAND}" >/dev/null || die "required command not found: ${COMMAND}"
done
[[ -d /etc/lazertag ]] || die 'run install.sh before deploying a release'
[[ -x /usr/local/libexec/lazertag/health-check ]] || die 'health-check is not installed'

exec 9>/run/lock/lazertag-deploy.lock
flock -n 9 || die 'another deploy or rollback is already running'

install -d -o root -g lazertag -m 0755 /opt/lazertag "${RELEASE_ROOT}"

ARCHIVE_HASH=$(sha256sum "${ARCHIVE}" | awk '{print $1}')
RELEASE_ID=release-${ARCHIVE_HASH:0:16}
RELEASE_DIR=${RELEASE_ROOT}/${RELEASE_ID}
INCOMING=${RELEASE_ROOT}/.incoming-${RELEASE_ID}-$$
NEXT_LINK=/opt/lazertag/.current-$$

cleanup() {
    local status=$?
    trap - EXIT INT TERM
    rm -f -- "${NEXT_LINK}"
    if [[ -n ${INCOMING:-} && -d ${INCOMING} ]]; then
        rm -rf -- "${INCOMING}"
    fi
    exit "${status}"
}
trap cleanup EXIT INT TERM

validate_archive() {
    local member normalized
    while IFS= read -r member; do
        normalized=${member#./}
        [[ -n ${normalized} ]] || continue
        [[ ${normalized} != /* ]] || die 'archive contains an absolute path'
        case "/${normalized}/" in
            */../*) die "archive contains path traversal: ${member}" ;;
        esac
    done < <(tar --list --gzip --file "${ARCHIVE}")
}

validate_extracted_links() {
    local link target resolved
    while IFS= read -r -d '' link; do
        target=$(readlink -- "${link}")
        resolved=$(readlink -m -- "$(dirname -- "${link}")/${target}")
        [[ ${resolved} == "${INCOMING}" || ${resolved} == "${INCOMING}"/* ]] \
            || die "archive symlink escapes the release: ${link} -> ${target}"
    done < <(find "${INCOMING}" -type l -print0)
}

activate_release() {
    local target=$1
    local release_name
    release_name=$(basename -- "${target}")
    [[ ${target} == "${RELEASE_ROOT}"/* && -d ${target} ]] || die 'refusing to activate an invalid release path'
    ln -s -- "releases/${release_name}" "${NEXT_LINK}"
    mv -Tf -- "${NEXT_LINK}" "${CURRENT_LINK}"
}

validate_archive
mkdir -- "${INCOMING}"
tar \
    --extract \
    --gzip \
    --file "${ARCHIVE}" \
    --directory "${INCOMING}" \
    --no-same-owner \
    --no-same-permissions
validate_extracted_links

[[ -f ${INCOMING}/LazerTagWebServer ]] || die 'release must contain LazerTagWebServer at archive root'
[[ -f ${INCOMING}/LazerTagWebServer.runtimeconfig.json ]] || die 'runtimeconfig is missing from release'
[[ -f ${INCOMING}/LazerTagWebServer.deps.json ]] || die 'dependency manifest is missing from release'
[[ -d ${INCOMING}/wwwroot ]] || die 'wwwroot is missing from release'

FILE_DESCRIPTION=$(file -b -- "${INCOMING}/LazerTagWebServer")
[[ ${FILE_DESCRIPTION} == *'ELF 64-bit'* && ${FILE_DESCRIPTION} == *'ARM aarch64'* ]] \
    || die "release apphost is not linux-arm64: ${FILE_DESCRIPTION}"

find "${INCOMING}" -type d -exec chmod 0750 {} +
find "${INCOMING}" -type f -exec chmod 0640 {} +
chmod 0750 "${INCOMING}/LazerTagWebServer"
printf '%s  %s\n' "${ARCHIVE_HASH}" "$(basename -- "${ARCHIVE}")" > "${INCOMING}/.archive.sha256"
chown -R root:lazertag "${INCOMING}"

if [[ -d ${RELEASE_DIR} ]]; then
    [[ -f ${RELEASE_DIR}/.archive.sha256 ]] || die "existing release lacks checksum marker: ${RELEASE_ID}"
    [[ $(awk 'NR == 1 { print $1 }' "${RELEASE_DIR}/.archive.sha256") == "${ARCHIVE_HASH}" ]] \
        || die "existing release checksum marker does not match: ${RELEASE_ID}"
    rm -rf -- "${INCOMING}"
    INCOMING=''
else
    mv -- "${INCOMING}" "${RELEASE_DIR}"
    INCOMING=''
fi

OLD_RELEASE=''
if [[ -L ${CURRENT_LINK} ]]; then
    OLD_RELEASE=$(readlink -f -- "${CURRENT_LINK}" || true)
    [[ -z ${OLD_RELEASE} || ${OLD_RELEASE} == "${RELEASE_ROOT}"/* ]] \
        || die 'current release symlink points outside the release root'
fi

if [[ ${OLD_RELEASE} == "${RELEASE_DIR}" ]]; then
    systemctl restart "${SERVICE}"
    /usr/local/libexec/lazertag/health-check --wait 45 --quiet
    printf 'Release %s was already active; service restart passed health check.\n' "${RELEASE_ID}"
    exit 0
fi

if ((MAKE_BACKUP == 1)); then
    /usr/local/sbin/lazertag-backup >/dev/null
fi

activate_release "${RELEASE_DIR}"

if systemctl restart "${SERVICE}" \
    && /usr/local/libexec/lazertag/health-check --wait 45 --quiet; then
    printf 'Activated %s.\n' "${RELEASE_ID}"
else
    printf 'Release %s failed its health check.\n' "${RELEASE_ID}" >&2
    if [[ -n ${OLD_RELEASE} && -d ${OLD_RELEASE} ]]; then
        activate_release "${OLD_RELEASE}"
        if systemctl restart "${SERVICE}" \
            && /usr/local/libexec/lazertag/health-check --wait 45 --quiet; then
            printf 'Automatically restored %s.\n' "$(basename -- "${OLD_RELEASE}")" >&2
        else
            printf 'CRITICAL: the previous release also failed to recover. Check journalctl -u lazertag.\n' >&2
        fi
    else
        systemctl stop "${SERVICE}" || true
        printf 'No previous release exists; the service has been stopped.\n' >&2
    fi
    exit 1
fi

CURRENT_RELEASE=$(readlink -f -- "${CURRENT_LINK}")
mapfile -t ALL_RELEASES < <(
    find "${RELEASE_ROOT}" -mindepth 1 -maxdepth 1 -type d -name 'release-*' -printf '%T@ %p\n' \
        | sort -nr \
        | cut -d' ' -f2-
)
RETAINED=0
for CANDIDATE in "${ALL_RELEASES[@]}"; do
    if [[ ${CANDIDATE} == "${CURRENT_RELEASE}" ]]; then
        continue
    fi
    ((RETAINED += 1))
    if ((RETAINED >= KEEP_RELEASES)); then
        [[ ${CANDIDATE} == "${RELEASE_ROOT}"/release-* && -d ${CANDIDATE} && ! -L ${CANDIDATE} ]] \
            || die 'refusing unsafe release cleanup path'
        rm -rf -- "${CANDIDATE}"
    fi
done
