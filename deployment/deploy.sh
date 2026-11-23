#!/usr/bin/env bash
set -Eeuo pipefail

SERVICE=lazertag.service
RELEASE_ROOT=/opt/lazertag/releases
CURRENT_LINK=/opt/lazertag/current
KEEP_RELEASES=5
HEALTH_CHECK=/usr/local/libexec/lazertag/health-check

usage() {
    cat <<'EOF'
Usage: sudo lazertag-deploy RELEASE.tar.gz [--sha256 DIGEST] [--keep-releases COUNT] [--no-backup]

Installs a flat linux-arm64 self-contained publish archive, atomically switches
/opt/lazertag/current, restarts the service, and rolls back automatically if the
/healthz readiness check does not pass.
EOF
}

die() {
    printf 'lazertag-deploy: %s\n' "$*" >&2
    exit 1
}

extract_release_archive() {
    # The caller supplies a private copy and a newly created, empty directory.
    python3 -I - "$1" "$2" <<'PY'
import os
from pathlib import Path, PurePosixPath, PureWindowsPath
import shutil
import sys
import tarfile

def unpack(source, destination):
    with open(source, "rb") as archive:
        os.chdir(destination)
        if hasattr(os, "geteuid") and os.geteuid() == 0:
            import pwd
            worker = pwd.getpwnam("nobody")
            os.chown(".", worker.pw_uid, worker.pw_gid)
            os.setgroups([])
            os.setgid(worker.pw_gid)
            os.setuid(worker.pw_uid)
        os.umask(0o077)
        with tarfile.open(fileobj=archive, mode="r:gz") as bundle:
            files, directories = set(), {PurePosixPath(".")}
            entries = []
            for member in bundle:
                path = PurePosixPath(member.name)
                if (path.is_absolute() or ".." in path.parts or "\\" in member.name
                        or PureWindowsPath(str(path)).drive or "\0" in member.name):
                    raise ValueError("invalid release member name")
                if member.type not in (tarfile.DIRTYPE, tarfile.REGTYPE, tarfile.AREGTYPE) or member.sparse is not None:
                    raise ValueError("release members must be ordinary files and directories")
                if path in files or any(parent in files for parent in path.parents):
                    raise ValueError("conflicting release member paths")
                directories.update(path.parents)
                if member.isdir():
                    directories.add(path)
                else:
                    if path in directories:
                        raise ValueError("release member conflicts with a directory")
                    files.add(path)
                entries.append((member, path))

            # Validate every header before writing. Create the files ourselves so tar
            # ownership, permission bits and link metadata never reach the filesystem.
            for member, path in entries:
                target = Path(path)
                if member.isdir():
                    target.mkdir(parents=True, exist_ok=True)
                else:
                    target.parent.mkdir(parents=True, exist_ok=True)
                    with bundle.extractfile(member) as content, target.open("xb") as output:
                        shutil.copyfileobj(content, output)

try:
    unpack(sys.argv[1], sys.argv[2])
except (OSError, EOFError, tarfile.TarError, ValueError) as exc:
    sys.exit(f"Invalid release archive: {exc}")
PY
}

stage_release_archive() {
    local source=$1 work=$2 expected=$3
    (umask 077; cat -- "${source}" > "${work}/release.tar.gz") || return 1
    ARCHIVE_HASH=$(sha256sum "${work}/release.tar.gz" | awk '{print $1}') || return 1
    [[ -z ${expected} || ${ARCHIVE_HASH} == "${expected}" ]] || die 'release checksum does not match the upload'
    (umask 077; mkdir -- "${work}/publish") || return 1
    extract_release_archive "${work}/release.tar.gz" "${work}/publish"
}

validate_release_files() {
    local root=$1 name
    for name in LazerTagWebServer LazerTagWebServer.dll LazerTagWebServer.runtimeconfig.json \
        LazerTagWebServer.deps.json GameTypes.xml wwwroot/index.html wwwroot/tv.html \
        wwwroot/css/app.css wwwroot/js/app.js wwwroot/js/signalr.min.js; do
        [[ -f ${root}/${name} && -s ${root}/${name} ]] || die "missing or empty release file: ${name}"
    done
    if grep -Fq '/js/snapshots.js' "${root}/wwwroot/index.html" "${root}/wwwroot/tv.html"; then
        [[ -s ${root}/wwwroot/js/snapshots.js ]] || die 'missing or empty release file: wwwroot/js/snapshots.js'
    fi
    python3 -I - "${root}/GameTypes.xml" <<'PY'
import sys
import xml.etree.ElementTree as ET

try:
    root = ET.parse(sys.argv[1]).getroot()
    if root.tag != "LazerSwarm" or root.findtext("FileInfo/FileType") != "GameTypes":
        raise ValueError("expected a LazerSwarm GameTypes catalog")
    modes = {node.attrib["Name"]: int(node.findtext("TeamCount", ""))
             for node in root.findall("GameTypes/GameType")}
    for teams, names in enumerate(("CUST OWNZ RESP SURV", "2TMS HDSK 2KNG 2TOZ 2TRS 2SRV",
                                   "3TMS HUNT 3KNG 3TOZ 3TRS 3SRV"), start=1):
        for name in names.split():
            if modes.get(name) != teams:
                raise ValueError(f"missing or incompatible mode {name}")
except (OSError, ET.ParseError, KeyError, ValueError) as exc:
    sys.exit(f"Invalid GameTypes.xml: {exc}")
PY
}

activate_release() {
    local target=$1
    local release_name
    release_name=$(basename -- "${target}")
    [[ ${target} == "${RELEASE_ROOT}"/* && -d ${target} ]] || die 'refusing to activate an invalid release path'
    ln -s -- "releases/${release_name}" "${NEXT_LINK}"
    mv -Tf -- "${NEXT_LINK}" "${CURRENT_LINK}"
}

activate_and_verify() {
    activate_release "${RELEASE_DIR}"

    if systemctl restart "${SERVICE}" \
        && "${HEALTH_CHECK}" --wait 45 --quiet; then
        printf 'Activated %s.\n' "${RELEASE_ID}"
    else
        printf 'Release %s failed its health check.\n' "${RELEASE_ID}" >&2
        if [[ -n ${OLD_RELEASE} && -d ${OLD_RELEASE} ]]; then
            activate_release "${OLD_RELEASE}"
            if systemctl restart "${SERVICE}" \
                && "${HEALTH_CHECK}" --wait 45 --quiet; then
                printf 'Automatically restored %s.\n' "$(basename -- "${OLD_RELEASE}")" >&2
            else
                printf 'CRITICAL: the previous release also failed to recover. Check journalctl -u lazertag.\n' >&2
            fi
        else
            systemctl stop "${SERVICE}" || true
            printf 'No previous release exists; the service has been stopped.\n' >&2
        fi
        return 1
    fi
}

check_running_arena_guard() {
    local state
    state=$(systemctl show --property=ActiveState --value "${SERVICE}") || return 1
    case "${state}" in
        inactive|failed) return 0 ;;
        active)
            if curl -fsS --max-time 5 http://127.0.0.1:5000/healthz/live |
                python3 -Ic 'import json,sys; sys.exit(json.load(sys.stdin).get("matchUpdateLock") is not True)'; then
                return 0
            fi ;;
    esac
    printf '%s\n' 'The running server cannot reserve an update window. Between games, stop lazertag.service once, then run this update again.' >&2
    return 1
}

# Deployment tests source the release checks without running the privileged installer.
[[ ${BASH_SOURCE[0]} == "$0" ]] || return 0

[[ ${EUID} -eq 0 ]] || die 'must run as root'
(($# >= 1)) || { usage >&2; exit 2; }

ARCHIVE=$1
shift
MAKE_BACKUP=1
EXPECTED_HASH=''

while (($#)); do
    case "$1" in
        --sha256)
            (($# >= 2)) && [[ $2 =~ ^[0-9a-f]{64}$ ]] || die '--sha256 requires a lowercase SHA-256 digest'
            EXPECTED_HASH=$2
            shift 2
            ;;
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

for COMMAND in flock sha256sum file python3 systemctl curl; do
    command -v "${COMMAND}" >/dev/null || die "required command not found: ${COMMAND}"
done
[[ -d /etc/lazertag ]] || die 'run install.sh before deploying a release'
[[ -x ${HEALTH_CHECK} ]] || die 'health-check is not installed'

exec 9>>/run/lock/lazertag-deploy.lock
flock -n 9 || die 'a game, pending results save, or another update is active'
check_running_arena_guard

install -d -o root -g lazertag -m 0755 /opt/lazertag "${RELEASE_ROOT}"

WORK_DIR=$(mktemp -d -- "${RELEASE_ROOT}/.deploy-XXXXXXXX")
NEXT_LINK=/opt/lazertag/.current-$$

cleanup() {
    local status=$?
    trap - EXIT INT TERM
    rm -f -- "${NEXT_LINK}"
    rm -rf -- "${WORK_DIR}"
    exit "${status}"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

stage_release_archive "${ARCHIVE}" "${WORK_DIR}" "${EXPECTED_HASH}"
RELEASE_ID=release-${ARCHIVE_HASH:0:16}
RELEASE_DIR=${RELEASE_ROOT}/${RELEASE_ID}
INCOMING=${WORK_DIR}/publish
validate_release_files "${INCOMING}"

FILE_DESCRIPTION=$(file -b -- "${INCOMING}/LazerTagWebServer")
[[ ${FILE_DESCRIPTION} == *'ELF 64-bit'* && ${FILE_DESCRIPTION} == *'ARM aarch64'* ]] \
    || die "release apphost is not linux-arm64: ${FILE_DESCRIPTION}"

# Keep the shared inode readable by the unprivileged game service after reboot.
printf '%s\n' 'f /run/lock/lazertag-deploy.lock 0644 root root -' > /etc/tmpfiles.d/lazertag-update-lock.conf
chmod 0644 /run/lock/lazertag-deploy.lock

find "${INCOMING}" -type d -exec chmod 0750 {} +
find "${INCOMING}" -type f -exec chmod 0640 {} +
chmod 0750 "${INCOMING}/LazerTagWebServer"
printf '%s  %s\n' "${ARCHIVE_HASH}" "$(basename -- "${ARCHIVE}")" > "${INCOMING}/.archive.sha256"
chown -R root:lazertag "${INCOMING}"

if [[ -d ${RELEASE_DIR} ]]; then
    [[ -f ${RELEASE_DIR}/.archive.sha256 ]] || die "existing release lacks checksum marker: ${RELEASE_ID}"
    [[ $(awk 'NR == 1 { print $1 }' "${RELEASE_DIR}/.archive.sha256") == "${ARCHIVE_HASH}" ]] \
        || die "existing release checksum marker does not match: ${RELEASE_ID}"
else
    mv -- "${INCOMING}" "${RELEASE_DIR}"
fi

OLD_RELEASE=''
if [[ -L ${CURRENT_LINK} ]]; then
    OLD_RELEASE=$(readlink -f -- "${CURRENT_LINK}" || true)
    [[ -z ${OLD_RELEASE} || ${OLD_RELEASE} == "${RELEASE_ROOT}"/* ]] \
        || die 'current release symlink points outside the release root'
fi

if [[ ${OLD_RELEASE} == "${RELEASE_DIR}" ]]; then
    systemctl restart "${SERVICE}"
    "${HEALTH_CHECK}" --wait 45 --quiet
    printf 'Release %s was already active; service restart passed health check.\n' "${RELEASE_ID}"
    exit 0
fi

if ((MAKE_BACKUP == 1)); then
    /usr/local/sbin/lazertag-backup >/dev/null
fi

activate_and_verify

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
