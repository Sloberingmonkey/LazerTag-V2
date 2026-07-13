#!/usr/bin/env bash
set -Eeuo pipefail

DESTINATION=/var/backups/lazertag
KEEP=14
STOP_SERVICE=1
SERVICE=lazertag.service

usage() {
    cat <<'EOF'
Usage: sudo lazertag-backup [--destination DIR] [--keep COUNT] [--online]

Backs up persistent arena data and root-owned configuration. By default the
service is stopped briefly for a consistent snapshot and restarted afterward.
Use --online only when the arena is idle and all data stores are known to use
atomic writes.
EOF
}

die() {
    printf 'lazertag-backup: %s\n' "$*" >&2
    exit 1
}

while (($#)); do
    case "$1" in
        --destination)
            (($# >= 2)) || die '--destination requires a directory'
            DESTINATION=$2
            shift 2
            ;;
        --keep)
            (($# >= 2)) || die '--keep requires a count'
            KEEP=$2
            shift 2
            ;;
        --online)
            STOP_SERVICE=0
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

[[ ${EUID} -eq 0 ]] || die 'must run as root'
[[ ${KEEP} =~ ^[1-9][0-9]*$ ]] || die '--keep must be a positive integer'
((KEEP <= 365)) || die '--keep cannot exceed 365'
command -v flock >/dev/null || die 'flock is required'
command -v tar >/dev/null || die 'tar is required'
command -v sha256sum >/dev/null || die 'sha256sum is required'

DESTINATION=$(readlink -m -- "${DESTINATION}")
[[ ${DESTINATION} == /* && ${DESTINATION} != / ]] || die 'destination must be a non-root absolute path'
install -d -o root -g root -m 0700 -- "${DESTINATION}"

exec 9>/run/lock/lazertag-backup.lock
flock -n 9 || die 'another backup is already running'

WAS_ACTIVE=0
RESTART_REQUIRED=0
cleanup() {
    local status=$?
    if ((RESTART_REQUIRED == 1)); then
        systemctl start "${SERVICE}" >/dev/null 2>&1 || true
    fi
    [[ -z ${TEMP_ARCHIVE:-} ]] || rm -f -- "${TEMP_ARCHIVE}"
    exit "${status}"
}
trap cleanup EXIT INT TERM

if systemctl is-active --quiet "${SERVICE}"; then
    WAS_ACTIVE=1
fi

if ((STOP_SERVICE == 1 && WAS_ACTIVE == 1)); then
    RESTART_REQUIRED=1
    systemctl stop "${SERVICE}"
fi

PATHS=(var/lib/lazertag)
[[ -f /etc/lazertag/lazertag.env ]] && PATHS+=(etc/lazertag/lazertag.env)
[[ -f /etc/lazertag/hotspot.conf ]] && PATHS+=(etc/lazertag/hotspot.conf)
[[ -f /etc/lazertag/hostapd.conf ]] && PATHS+=(etc/lazertag/hostapd.conf)
[[ -f /etc/lazertag/dnsmasq.conf ]] && PATHS+=(etc/lazertag/dnsmasq.conf)
[[ -f /etc/lazertag/lazertag-firewall.nft ]] && PATHS+=(etc/lazertag/lazertag-firewall.nft)
[[ -f /etc/NetworkManager/conf.d/90-lazertag-hotspot-unmanaged.conf ]] \
    && PATHS+=(etc/NetworkManager/conf.d/90-lazertag-hotspot-unmanaged.conf)

STAMP=$(date -u +%Y%m%dT%H%M%SZ)
ARCHIVE=${DESTINATION}/lazertag-backup-${STAMP}.tar.gz
TEMP_ARCHIVE=${ARCHIVE}.tmp
umask 077

tar \
    --create \
    --gzip \
    --file "${TEMP_ARCHIVE}" \
    --directory / \
    --acls \
    --xattrs \
    --numeric-owner \
    "${PATHS[@]}"

tar --list --gzip --file "${TEMP_ARCHIVE}" >/dev/null
mv -- "${TEMP_ARCHIVE}" "${ARCHIVE}"
TEMP_ARCHIVE=''
sha256sum "${ARCHIVE}" > "${ARCHIVE}.sha256"
chmod 0600 "${ARCHIVE}" "${ARCHIVE}.sha256"

if ((RESTART_REQUIRED == 1)); then
    systemctl start "${SERVICE}"
    /usr/local/libexec/lazertag/health-check --wait 45 --quiet
    RESTART_REQUIRED=0
fi

mapfile -t OLD_BACKUPS < <(
    find "${DESTINATION}" -maxdepth 1 -type f -name 'lazertag-backup-*.tar.gz' -printf '%T@ %p\n' \
        | sort -nr \
        | tail -n "+$((KEEP + 1))" \
        | cut -d' ' -f2-
)
for OLD_BACKUP in "${OLD_BACKUPS[@]}"; do
    [[ ${OLD_BACKUP} == "${DESTINATION}"/lazertag-backup-*.tar.gz ]] || die 'refusing unsafe backup cleanup path'
    rm -f -- "${OLD_BACKUP}" "${OLD_BACKUP}.sha256"
done

printf '%s\n' "${ARCHIVE}"
