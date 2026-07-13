#!/usr/bin/env bash
set -Eeuo pipefail

RELEASE_ARCHIVE=''
OPERATOR_PIN_FILE=${ARENA_OPERATOR_PIN_FILE:-}
WIFI_PASSWORD_FILE=${LAZERTAG_WIFI_PASSWORD_FILE:-}
CONFIGURE_HOTSPOT=1
INSTALL_PACKAGES=1
FORCE_HOTSPOT=0

usage() {
    cat <<'EOF'
Usage: sudo ./install.sh [options]

Options:
  --release FILE                 Deploy a flat linux-arm64 publish tarball.
  --operator-pin-file FILE       Root-only file containing an 8-12 digit PIN.
                                 If omitted on a fresh install, a PIN is generated.
  --hotspot-password-file FILE   Root-only file containing the WPA2 password.
  --external-network             Do not configure the Pi's wlan0 hotspot.
  --force-hotspot                Permit replacing wlan0 while SSH is using it.
  --skip-packages                Do not run apt-get (for image-building only).
  -h, --help                     Show this help.

Secrets are intentionally accepted from files, not command-line values.
EOF
}

die() {
    printf 'install-lazertag: %s\n' "$*" >&2
    exit 1
}

while (($#)); do
    case "$1" in
        --release)
            (($# >= 2)) || die '--release requires a file'
            RELEASE_ARCHIVE=$2
            shift 2
            ;;
        --operator-pin-file)
            (($# >= 2)) || die '--operator-pin-file requires a file'
            OPERATOR_PIN_FILE=$2
            shift 2
            ;;
        --hotspot-password-file)
            (($# >= 2)) || die '--hotspot-password-file requires a file'
            WIFI_PASSWORD_FILE=$2
            shift 2
            ;;
        --external-network)
            CONFIGURE_HOTSPOT=0
            shift
            ;;
        --force-hotspot)
            FORCE_HOTSPOT=1
            shift
            ;;
        --skip-packages)
            INSTALL_PACKAGES=0
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
SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)

for FILE in \
    lazertag.service \
    lazertag-firewall.service \
    lazertag-firewall.nft \
    lazertag.env.example \
    hotspot.conf \
    configure-hotspot.sh \
    hotspot-interface.sh \
    hotspot-check.sh \
    lazertag-hotspot.service \
    lazertag-hostapd.service \
    lazertag-dnsmasq.service \
    firewall-apply.sh \
    health-check.sh \
    backup.sh \
    deploy.sh \
    rollback.sh \
    xinitrc \
    PI_SETUP.md; do
    [[ -f ${SCRIPT_DIR}/${FILE} ]] || die "deployment bundle is incomplete: ${FILE} is missing"
done

[[ $(uname -m) == aarch64 ]] || die 'this installer requires 64-bit Raspberry Pi OS (aarch64)'
[[ -r /etc/os-release ]] || die 'cannot identify the operating system'
# shellcheck disable=SC1091
source /etc/os-release
OS_MAJOR=${VERSION_ID%%.*}
[[ ${OS_MAJOR} =~ ^[0-9]+$ && ${OS_MAJOR} -ge 13 ]] \
    || die 'Raspberry Pi OS Trixie / Debian 13 or newer is required'

if [[ -n ${RELEASE_ARCHIVE} ]]; then
    [[ -r ${RELEASE_ARCHIVE} && -f ${RELEASE_ARCHIVE} ]] || die "cannot read release: ${RELEASE_ARCHIVE}"
    RELEASE_ARCHIVE=$(readlink -f -- "${RELEASE_ARCHIVE}")
fi

check_secret_file() {
    local file=$1
    local label=$2
    [[ -f ${file} && -r ${file} ]] || die "cannot read ${label} file: ${file}"
    if find "${file}" -prune -perm /077 -print -quit | grep -q .; then
        die "${label} file must be mode 0600"
    fi
    [[ $(stat -c '%u' -- "${file}") == 0 ]] || die "${label} file must be owned by root"
}

if [[ -n ${OPERATOR_PIN_FILE} ]]; then
    check_secret_file "${OPERATOR_PIN_FILE}" 'operator PIN'
fi
if ((CONFIGURE_HOTSPOT == 1)); then
    [[ -n ${WIFI_PASSWORD_FILE} ]] || die '--hotspot-password-file is required unless --external-network is used'
    check_secret_file "${WIFI_PASSWORD_FILE}" 'Wi-Fi password'
fi

if ((INSTALL_PACKAGES == 1)); then
    export DEBIAN_FRONTEND=noninteractive
    apt-get update
    apt-get install -y --no-install-recommends \
        ca-certificates \
        curl \
        file \
        iproute2 \
        iw \
        dnsmasq-base \
        hostapd \
        network-manager \
        nftables \
        rfkill \
        tar \
        util-linux \
        wpasupplicant
    systemctl enable --now NetworkManager.service
fi

for COMMAND in curl dnsmasq file flock hostapd hostapd_cli ip iw nmcli nft rfkill sha256sum systemctl tar wpa_passphrase; do
    command -v "${COMMAND}" >/dev/null || die "required command not found: ${COMMAND}"
done

if id lazertag >/dev/null 2>&1; then
    LAZERTAG_UID=$(id -u lazertag)
    ((LAZERTAG_UID < 1000)) || die 'existing lazertag account is not a system user; resolve it manually'
    usermod --home /var/lib/lazertag --shell /usr/sbin/nologin lazertag
else
    getent group lazertag >/dev/null || groupadd --system lazertag
    useradd \
        --system \
        --gid lazertag \
        --home-dir /var/lib/lazertag \
        --shell /usr/sbin/nologin \
        lazertag
fi

if getent group lazertag-dnsmasq >/dev/null; then
    DNSMASQ_GROUP=lazertag-dnsmasq
else
    groupadd --system lazertag-dnsmasq
    DNSMASQ_GROUP=lazertag-dnsmasq
fi
if id lazertag-dnsmasq >/dev/null 2>&1; then
    DNSMASQ_UID=$(id -u lazertag-dnsmasq)
    ((DNSMASQ_UID < 1000)) || die 'existing lazertag-dnsmasq account is not a system user; resolve it manually'
    usermod --gid "${DNSMASQ_GROUP}" --home /var/lib/lazertag-dnsmasq --shell /usr/sbin/nologin lazertag-dnsmasq
else
    useradd \
        --system \
        --gid "${DNSMASQ_GROUP}" \
        --home-dir /var/lib/lazertag-dnsmasq \
        --shell /usr/sbin/nologin \
        lazertag-dnsmasq
fi

install -d -o root -g lazertag -m 0750 /opt/lazertag /opt/lazertag/releases
install -d -o lazertag -g lazertag -m 0750 /var/lib/lazertag
# dnsmasq opens the lease database before it drops from root to the dedicated
# account. Keep this one-purpose state directory and lease file root-owned so
# startup does not require the broad CAP_DAC_OVERRIDE capability. The daemon
# retains the already-open descriptor after dropping privileges.
DNSMASQ_STATE_DIR=/var/lib/lazertag-dnsmasq
DNSMASQ_LEASE_FILE=${DNSMASQ_STATE_DIR}/dnsmasq.leases
if [[ -L ${DNSMASQ_STATE_DIR} || ( -e ${DNSMASQ_STATE_DIR} && ! -d ${DNSMASQ_STATE_DIR} ) ]]; then
    die "dnsmasq state path is not a regular directory: ${DNSMASQ_STATE_DIR}"
fi
install -d -o root -g root -m 0750 "${DNSMASQ_STATE_DIR}"
if [[ -L ${DNSMASQ_LEASE_FILE} || ( -e ${DNSMASQ_LEASE_FILE} && ! -f ${DNSMASQ_LEASE_FILE} ) ]]; then
    die "dnsmasq lease path is not a regular file: ${DNSMASQ_LEASE_FILE}"
fi
if [[ ! -e ${DNSMASQ_LEASE_FILE} ]]; then
    install -o root -g root -m 0600 /dev/null "${DNSMASQ_LEASE_FILE}"
else
    chown root:root -- "${DNSMASQ_LEASE_FILE}"
    chmod 0600 -- "${DNSMASQ_LEASE_FILE}"
fi
install -d -o root -g root -m 0750 /etc/lazertag
install -d -o root -g root -m 0755 /usr/local/libexec/lazertag /usr/local/share/doc/lazertag

install -o root -g root -m 0755 "${SCRIPT_DIR}/health-check.sh" /usr/local/libexec/lazertag/health-check
install -o root -g root -m 0755 "${SCRIPT_DIR}/firewall-apply.sh" /usr/local/libexec/lazertag/firewall-apply
install -o root -g root -m 0755 "${SCRIPT_DIR}/xinitrc" /usr/local/libexec/lazertag/xinitrc
install -o root -g root -m 0755 "${SCRIPT_DIR}/hotspot-interface.sh" /usr/local/libexec/lazertag/hotspot-interface
install -o root -g root -m 0755 "${SCRIPT_DIR}/hotspot-check.sh" /usr/local/libexec/lazertag/hotspot-check
install -o root -g root -m 0755 "${SCRIPT_DIR}/configure-hotspot.sh" /usr/local/sbin/lazertag-configure-hotspot
install -o root -g root -m 0755 "${SCRIPT_DIR}/backup.sh" /usr/local/sbin/lazertag-backup
install -o root -g root -m 0755 "${SCRIPT_DIR}/deploy.sh" /usr/local/sbin/lazertag-deploy
install -o root -g root -m 0755 "${SCRIPT_DIR}/rollback.sh" /usr/local/sbin/lazertag-rollback
install -o root -g root -m 0644 "${SCRIPT_DIR}/PI_SETUP.md" /usr/local/share/doc/lazertag/PI_SETUP.md
install -o root -g root -m 0644 "${SCRIPT_DIR}/lazertag.env.example" /usr/local/share/doc/lazertag/lazertag.env.example
install -o root -g root -m 0644 "${SCRIPT_DIR}/lazertag.service" /etc/systemd/system/lazertag.service
install -o root -g root -m 0644 "${SCRIPT_DIR}/lazertag-hotspot.service" /etc/systemd/system/lazertag-hotspot.service
install -o root -g root -m 0644 "${SCRIPT_DIR}/lazertag-hostapd.service" /etc/systemd/system/lazertag-hostapd.service
install -o root -g root -m 0644 "${SCRIPT_DIR}/lazertag-dnsmasq.service" /etc/systemd/system/lazertag-dnsmasq.service
install -o root -g root -m 0644 "${SCRIPT_DIR}/lazertag-firewall.service" /etc/systemd/system/lazertag-firewall.service
install -o root -g root -m 0644 "${SCRIPT_DIR}/lazertag-firewall.nft" /etc/lazertag/lazertag-firewall.nft

if [[ ! -f /etc/lazertag/hotspot.conf ]]; then
    install -o root -g root -m 0644 "${SCRIPT_DIR}/hotspot.conf" /etc/lazertag/hotspot.conf
fi

generate_pin() {
    local pin=''
    local byte
    local -a random_bytes
    while ((${#pin} < 10)); do
        read -r -a random_bytes < <(od -An -N32 -tu1 /dev/urandom)
        for byte in "${random_bytes[@]}"; do
            # Rejection sampling avoids modulo bias: 250 is divisible by 10.
            ((byte < 250)) || continue
            pin+=$((byte % 10))
            ((${#pin} == 10)) && break
        done
    done
    printf '%s' "${pin}"
}

validate_new_pin() {
    local pin=$1
    [[ ${pin} =~ ^[0-9]{8,12}$ ]] || die 'a new operator PIN must contain 8-12 digits'
    [[ ${pin} != 00000000 && ${pin} != 11111111 && ${pin} != 12345678 \
        && ${pin} != 87654321 && ${pin} != 01234567 ]] \
        || die 'choose a less predictable operator PIN'
}

upsert_setting() {
    local file=$1
    local key=$2
    local value=$3
    local temporary
    temporary=$(mktemp "${file}.XXXXXX")
    awk -v key="${key}" -v value="${value}" '
        BEGIN { found = 0 }
        index($0, key "=") == 1 {
            if (!found) print key "=" value
            found = 1
            next
        }
        { print }
        END { if (!found) print key "=" value }
    ' "${file}" > "${temporary}"
    chown root:root "${temporary}"
    chmod 0600 "${temporary}"
    mv -- "${temporary}" "${file}"
}

ENV_FILE=/etc/lazertag/lazertag.env
PIN_WAS_GENERATED=0
if [[ ! -f ${ENV_FILE} ]]; then
    install -o root -g root -m 0600 "${SCRIPT_DIR}/lazertag.env.example" "${ENV_FILE}"
    if [[ -n ${OPERATOR_PIN_FILE} ]]; then
        OPERATOR_PIN=''
        IFS= read -r OPERATOR_PIN < "${OPERATOR_PIN_FILE}" || [[ -n ${OPERATOR_PIN} ]]
        OPERATOR_PIN=${OPERATOR_PIN%$'\r'}
        validate_new_pin "${OPERATOR_PIN}"
else
        while :; do
            OPERATOR_PIN=$(generate_pin)
            if [[ ${OPERATOR_PIN} != 0000000000 && ${OPERATOR_PIN} != 1111111111 \
                && ${OPERATOR_PIN} != 1234567890 && ${OPERATOR_PIN} != 0987654321 ]]; then
                break
            fi
        done
        PIN_WAS_GENERATED=1
        PIN_OUTPUT=/root/lazertag-initial-operator-pin.txt
        umask 077
        printf '%s\n' "${OPERATOR_PIN}" > "${PIN_OUTPUT}"
        chmod 0600 "${PIN_OUTPUT}"
    fi
    upsert_setting "${ENV_FILE}" Arena__OperatorPin "${OPERATOR_PIN}"
    OPERATOR_PIN=''
elif [[ -n ${OPERATOR_PIN_FILE} ]]; then
    OPERATOR_PIN=''
    IFS= read -r OPERATOR_PIN < "${OPERATOR_PIN_FILE}" || [[ -n ${OPERATOR_PIN} ]]
    OPERATOR_PIN=${OPERATOR_PIN%$'\r'}
    validate_new_pin "${OPERATOR_PIN}"
    upsert_setting "${ENV_FILE}" Arena__OperatorPin "${OPERATOR_PIN}"
    OPERATOR_PIN=''
elif ! grep -Eq '^Arena__OperatorPin=.+$' "${ENV_FILE}" \
    || grep -Eq '^Arena__OperatorPin=(REPLACE_WITH_A_UNIQUE_PIN|CHANGE_ME|123456)$' "${ENV_FILE}"; then
    die 'existing environment file has no valid operator PIN; supply --operator-pin-file'
fi

# Preserve an existing PIN during upgrades while repairing all non-secret,
# deployment-owned settings to the supported production values.
upsert_setting "${ENV_FILE}" ASPNETCORE_ENVIRONMENT Production
upsert_setting "${ENV_FILE}" ASPNETCORE_URLS http://0.0.0.0:5000
upsert_setting "${ENV_FILE}" DOTNET_SYSTEM_GLOBALIZATION_INVARIANT 1
upsert_setting "${ENV_FILE}" DOTNET_EnableDiagnostics 0
upsert_setting "${ENV_FILE}" Arena__OperatorSessionHours 12
upsert_setting "${ENV_FILE}" Arena__RelayAddress 192.168.4.100:8266
upsert_setting "${ENV_FILE}" Arena__DataDirectory /var/lib/lazertag
chown root:root "${ENV_FILE}"
chmod 0600 "${ENV_FILE}"

systemctl daemon-reload
# The distribution hostapd unit is not used; the dedicated LTAR unit has
# explicit ordering, configuration, and hardening.
systemctl disable --now hostapd.service >/dev/null 2>&1 || true

if ((CONFIGURE_HOTSPOT == 1)); then
    systemctl enable lazertag-firewall.service >/dev/null
    systemctl restart lazertag-firewall.service
    HOTSPOT_ARGS=(--config /etc/lazertag/hotspot.conf --password-file "${WIFI_PASSWORD_FILE}")
    ((FORCE_HOTSPOT == 1)) && HOTSPOT_ARGS+=(--force)
    /usr/local/sbin/lazertag-configure-hotspot "${HOTSPOT_ARGS[@]}"
    /usr/local/libexec/lazertag/hotspot-check --wait 30 --quiet
else
    systemctl disable --now \
        lazertag-dnsmasq.service \
        lazertag-hostapd.service \
        lazertag-hotspot.service \
        lazertag-firewall.service >/dev/null 2>&1 || true
    rm -f /etc/NetworkManager/conf.d/90-lazertag-hotspot-unmanaged.conf
    nmcli general reload conf >/dev/null 2>&1 || true
    nmcli device set wlan0 managed yes >/dev/null 2>&1 || true
fi

if [[ -n ${RELEASE_ARCHIVE} ]]; then
    systemctl enable lazertag.service
    /usr/local/sbin/lazertag-deploy "${RELEASE_ARCHIVE}"
elif [[ -x /opt/lazertag/current/LazerTagWebServer ]]; then
    systemctl enable lazertag.service
    systemctl restart lazertag.service
    /usr/local/libexec/lazertag/health-check --wait 45 --quiet
else
    printf 'Platform installed. Deploy a release with: sudo lazertag-deploy RELEASE.tar.gz\n'
fi

if ((PIN_WAS_GENERATED == 1)); then
    printf 'A generated operator PIN was written to /root/lazertag-initial-operator-pin.txt (mode 0600).\n'
    printf 'Record it in the arena password manager, then securely remove that one-time file.\n'
fi

printf 'LazerTag deployment layer installed successfully.\n'
