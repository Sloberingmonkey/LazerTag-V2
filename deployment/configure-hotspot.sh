#!/usr/bin/env bash
set -Eeuo pipefail

usage() {
    cat <<'EOF'
Usage: sudo configure-hotspot [--config FILE] --password-file FILE [--force]

Creates or repairs the secured LazerTag hostapd/dnsmasq access point.
--force permits reconfiguring the interface currently carrying the SSH session;
this will normally disconnect that session and should be used only from a console.
EOF
}

die() {
    printf 'configure-hotspot: %s\n' "$*" >&2
    exit 1
}

[[ ${EUID} -eq 0 ]] || die 'must run as root'

SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
CONFIG_FILE=${LAZERTAG_HOTSPOT_CONFIG:-/etc/lazertag/hotspot.conf}
PASSWORD_FILE=${LAZERTAG_WIFI_PASSWORD_FILE:-}
FORCE=0

while (($#)); do
    case "$1" in
        --config)
            (($# >= 2)) || die '--config requires a file'
            CONFIG_FILE=$2
            shift 2
            ;;
        --password-file)
            (($# >= 2)) || die '--password-file requires a file'
            PASSWORD_FILE=$2
            shift 2
            ;;
        --force)
            FORCE=1
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

if [[ ! -r ${CONFIG_FILE} && -r ${SCRIPT_DIR}/hotspot.conf ]]; then
    CONFIG_FILE=${SCRIPT_DIR}/hotspot.conf
fi
[[ -r ${CONFIG_FILE} ]] || die "cannot read hotspot config: ${CONFIG_FILE}"
[[ -n ${PASSWORD_FILE} && -f ${PASSWORD_FILE} && -r ${PASSWORD_FILE} ]] || die 'a readable regular --password-file is required'

# This is a root-owned shell assignment file installed by install.sh.
# shellcheck disable=SC1090
source "${CONFIG_FILE}"

: "${HOTSPOT_CONNECTION:?missing HOTSPOT_CONNECTION}"
: "${HOTSPOT_INTERFACE:?missing HOTSPOT_INTERFACE}"
: "${HOTSPOT_SSID:?missing HOTSPOT_SSID}"
: "${HOTSPOT_BAND:?missing HOTSPOT_BAND}"
: "${HOTSPOT_CHANNEL:?missing HOTSPOT_CHANNEL}"
: "${HOTSPOT_ADDRESS:?missing HOTSPOT_ADDRESS}"
: "${HOTSPOT_DHCP_RANGE:?missing HOTSPOT_DHCP_RANGE}"
: "${HOTSPOT_RELAY_ADDRESS:?missing HOTSPOT_RELAY_ADDRESS}"
: "${HOTSPOT_SECURITY:?missing HOTSPOT_SECURITY}"
HOTSPOT_COUNTRY=${HOTSPOT_COUNTRY:-US}

[[ ${HOTSPOT_ADDRESS} == '192.168.4.1/24' ]] || die 'HOTSPOT_ADDRESS must remain 192.168.4.1/24'
[[ ${HOTSPOT_RELAY_ADDRESS} == '192.168.4.100' ]] || die 'HOTSPOT_RELAY_ADDRESS must remain 192.168.4.100'
[[ ${HOTSPOT_DHCP_RANGE} == '192.168.4.20,192.168.4.99' ]] || die 'DHCP range must exclude the relay at .100'
[[ ${HOTSPOT_SECURITY} == 'wpa2' ]] || die 'open or legacy-WPA arena Wi-Fi is not supported'
[[ ${HOTSPOT_INTERFACE} =~ ^[a-zA-Z0-9_.:-]+$ ]] || die 'invalid hotspot interface name'
[[ ${HOTSPOT_COUNTRY} =~ ^[A-Z]{2}$ ]] || die 'HOTSPOT_COUNTRY must be a two-letter uppercase country code'
[[ ${HOTSPOT_BAND} == 'bg' ]] || die 'the built-in Pi 3 hotspot must use the 2.4 GHz bg band'
[[ ${HOTSPOT_CHANNEL} =~ ^(1|6|11)$ ]] || die 'HOTSPOT_CHANNEL must be non-overlapping 2.4 GHz channel 1, 6, or 11'

SSID_LENGTH=${#HOTSPOT_SSID}
((SSID_LENGTH >= 1 && SSID_LENGTH <= 32)) || die 'HOTSPOT_SSID must contain 1-32 ASCII characters'
LC_ALL=C grep -Eq '^[ -~]+$' <<<"${HOTSPOT_SSID}" || die 'HOTSPOT_SSID must contain printable ASCII only'

for COMMAND in dnsmasq flock hostapd hostapd_cli ip iw nmcli rfkill systemctl wpa_passphrase; do
    command -v "${COMMAND}" >/dev/null || die "required command not found: ${COMMAND}"
done
systemctl is-active --quiet NetworkManager || die 'NetworkManager is not active for Ethernet administration'
NM_DEVICE_LIST=$(nmcli -t -f DEVICE device status)
grep -Fxq "${HOTSPOT_INTERFACE}" <<<"${NM_DEVICE_LIST}" || die "interface not found: ${HOTSPOT_INTERFACE}"

exec 9>/run/lock/lazertag-network.lock
flock -n 9 || die 'another hotspot configuration is already running'

if [[ -n ${SSH_CONNECTION:-} && ${FORCE} -ne 1 ]]; then
    SSH_CLIENT_IP=${SSH_CONNECTION%% *}
    SSH_ROUTE=$(ip route get "${SSH_CLIENT_IP}" 2>/dev/null || true)
    if [[ ${SSH_ROUTE} == *" dev ${HOTSPOT_INTERFACE} "* ]]; then
        die "the current SSH session uses ${HOTSPOT_INTERFACE}; reconnect over Ethernet or run from the local console"
    fi
fi

if find "${PASSWORD_FILE}" -prune -perm /077 -print -quit | grep -q .; then
    die 'password file must not be accessible by group or other users (use chmod 600)'
fi
[[ $(stat -c '%u' -- "${PASSWORD_FILE}") == 0 ]] || die 'password file must be owned by root'

WIFI_PASSWORD=''
WPA_PSK=''
mapfile -t PASSWORD_LINES < "${PASSWORD_FILE}"
((${#PASSWORD_LINES[@]} == 1)) || die 'password file must contain exactly one line'
WIFI_PASSWORD=${PASSWORD_LINES[0]}
unset PASSWORD_LINES
WIFI_PASSWORD=${WIFI_PASSWORD%$'\r'}
PASSWORD_LENGTH=${#WIFI_PASSWORD}
if [[ ${WIFI_PASSWORD} =~ ^[[:xdigit:]]{64}$ ]]; then
    WPA_PSK=${WIFI_PASSWORD,,}
elif ((PASSWORD_LENGTH >= 8 && PASSWORD_LENGTH <= 63)) \
    && LC_ALL=C grep -Eq '^[ -~]+$' <<<"${WIFI_PASSWORD}"; then
    WPA_PSK=$(
        printf '%s\n' "${WIFI_PASSWORD}" \
            | wpa_passphrase "${HOTSPOT_SSID}" \
            | sed -nE 's/^[[:space:]]*psk=([0-9a-f]{64})$/\1/p'
    )
else
    die 'WPA2 password must be 8-63 printable ASCII characters or exactly 64 hexadecimal digits'
fi
[[ ${WPA_PSK} =~ ^[0-9a-f]{64}$ ]] || die 'failed to derive the WPA2 key'
trap 'WIFI_PASSWORD=; WPA_PSK=' EXIT

IW_CAPABILITIES=$(iw list 2>/dev/null) || die 'could not read wireless capabilities'
if ! grep -Eq '^\s*\* AP$' <<<"${IW_CAPABILITIES}"; then
    die "${HOTSPOT_INTERFACE} does not report access-point capability"
fi

for UNIT in lazertag-hotspot.service lazertag-firewall.service lazertag-hostapd.service lazertag-dnsmasq.service; do
    systemctl cat "${UNIT}" >/dev/null 2>&1 || die "required service is not installed: ${UNIT}"
done

install -d -o root -g root -m 0750 /etc/lazertag
install -d -o root -g root -m 0755 /etc/NetworkManager/conf.d

TRANSACTION_DIR=$(mktemp -d /etc/lazertag/.hotspot-transaction.XXXXXX)
HOSTAPD_NEW=${TRANSACTION_DIR}/hostapd.conf
DNSMASQ_NEW=${TRANSACTION_DIR}/dnsmasq.conf
UNMANAGED_NEW=${TRANSACTION_DIR}/90-lazertag-hotspot-unmanaged.conf

cat > "${HOSTAPD_NEW}" <<EOF
# Generated by lazertag-configure-hotspot. Contains a reusable WPA2 key.
interface=${HOTSPOT_INTERFACE}
driver=nl80211
ctrl_interface=/run/lazertag-hostapd
ctrl_interface_group=0
ssid=${HOTSPOT_SSID}
country_code=${HOTSPOT_COUNTRY}
ieee80211d=1
hw_mode=g
channel=${HOTSPOT_CHANNEL}
ieee80211n=1
wmm_enabled=1
auth_algs=1
ignore_broadcast_ssid=0
ap_isolate=1
wps_state=0
wpa=2
wpa_key_mgmt=WPA-PSK
rsn_pairwise=CCMP
ieee80211w=0
wpa_psk=${WPA_PSK}
EOF
chmod 0600 "${HOSTAPD_NEW}"

cat > "${DNSMASQ_NEW}" <<EOF
# Generated by lazertag-configure-hotspot.
interface=${HOTSPOT_INTERFACE}
bind-dynamic
listen-address=192.168.4.1
port=53
domain-needed
bogus-priv
no-resolv
no-hosts
cache-size=0
dhcp-authoritative
dhcp-range=${HOTSPOT_DHCP_RANGE},255.255.255.0,8h
dhcp-option=option:router,192.168.4.1
dhcp-option=option:dns-server,192.168.4.1
dhcp-option=option:netmask,255.255.255.0
dhcp-lease-max=80
dhcp-leasefile=/var/lib/lazertag-dnsmasq/dnsmasq.leases
log-facility=-
log-dhcp
EOF
chmod 0644 "${DNSMASQ_NEW}"

cat > "${UNMANAGED_NEW}" <<EOF
# The arena WLAN is owned by hostapd, not NetworkManager/wpa_supplicant.
[keyfile]
unmanaged-devices=interface-name:${HOTSPOT_INTERFACE}
EOF
chmod 0644 "${UNMANAGED_NEW}"

dnsmasq --test --conf-file="${DNSMASQ_NEW}" >/dev/null
grep -Fxq 'wpa=2' "${HOSTAPD_NEW}" || die 'generated hostapd config is not WPA2-only'
grep -Fxq 'wpa_key_mgmt=WPA-PSK' "${HOSTAPD_NEW}" || die 'generated hostapd config has unexpected key management'
grep -Fxq 'rsn_pairwise=CCMP' "${HOSTAPD_NEW}" || die 'generated hostapd config is not CCMP-only'
grep -Fxq 'ieee80211w=0' "${HOSTAPD_NEW}" || die 'generated hostapd config did not disable unsupported Pi 3 PMF'
if grep -Eq '(^|[^a-z_])wpa_passphrase=' "${HOSTAPD_NEW}"; then
    die 'refusing to store a plaintext Wi-Fi passphrase'
fi

HOSTAPD_CONFIG=/etc/lazertag/hostapd.conf
DNSMASQ_CONFIG=/etc/lazertag/dnsmasq.conf
UNMANAGED_CONFIG=/etc/NetworkManager/conf.d/90-lazertag-hotspot-unmanaged.conf
PREVIOUS_STACK_ACTIVE=0
systemctl is-active --quiet lazertag-hostapd.service && PREVIOUS_STACK_ACTIVE=1
LEGACY_PROFILE_PRESENT=0
LEGACY_PROFILE_ACTIVE=0
LEGACY_AUTOCONNECT=yes
LEGACY_STORED_PSK=''
NM_CONNECTION_NAMES=$(nmcli -t -f NAME connection show)
if grep -Fxq "${HOTSPOT_CONNECTION}" <<<"${NM_CONNECTION_NAMES}"; then
    LEGACY_PROFILE_PRESENT=1
    LEGACY_AUTOCONNECT=$(nmcli -g connection.autoconnect connection show "${HOTSPOT_CONNECTION}" 2>/dev/null || printf 'yes')
    NM_ACTIVE_CONNECTION_NAMES=$(nmcli -t -f NAME connection show --active)
    grep -Fxq "${HOTSPOT_CONNECTION}" <<<"${NM_ACTIVE_CONNECTION_NAMES}" && LEGACY_PROFILE_ACTIVE=1
    LEGACY_STORED_PSK=$(nmcli --show-secrets --terse --escape no \
        --get-values 802-11-wireless-security.psk \
        connection show "${HOTSPOT_CONNECTION}" 2>/dev/null || true)
    if [[ -n ${LEGACY_STORED_PSK} && ${LEGACY_STORED_PSK} != "${WIFI_PASSWORD}" ]]; then
        die 'password file does not match the existing LazerTag profile; use the password already stored in the ESP32'
    fi
    LEGACY_STORED_PSK=''
fi

for FILE in "${HOSTAPD_CONFIG}" "${DNSMASQ_CONFIG}" "${UNMANAGED_CONFIG}"; do
    NAME=$(basename -- "${FILE}")
    if [[ -e ${FILE} ]]; then
        cp -a -- "${FILE}" "${TRANSACTION_DIR}/previous-${NAME}"
    else
        : > "${TRANSACTION_DIR}/absent-${NAME}"
    fi
done

SWITCH_STARTED=0
SUCCESS=0
rollback() {
    local status=$?
    trap - EXIT
    if ((status != 0 && SWITCH_STARTED == 1 && SUCCESS == 0)); then
        printf 'Hotspot activation failed; restoring the previous network configuration.\n' >&2
        set +e
        systemctl stop lazertag-dnsmasq.service lazertag-hostapd.service lazertag-hotspot.service
        for FILE in "${HOSTAPD_CONFIG}" "${DNSMASQ_CONFIG}" "${UNMANAGED_CONFIG}"; do
            NAME=$(basename -- "${FILE}")
            if [[ -e ${TRANSACTION_DIR}/previous-${NAME} ]]; then
                cp -a -- "${TRANSACTION_DIR}/previous-${NAME}" "${FILE}"
            else
                rm -f -- "${FILE}"
            fi
        done
        nmcli general reload conf >/dev/null 2>&1 || true
        if ((PREVIOUS_STACK_ACTIVE == 1)); then
            nmcli device set "${HOTSPOT_INTERFACE}" managed no >/dev/null 2>&1 || true
            systemctl start lazertag-hotspot.service lazertag-firewall.service lazertag-hostapd.service lazertag-dnsmasq.service
        elif ((LEGACY_PROFILE_PRESENT == 1)); then
            nmcli device set "${HOTSPOT_INTERFACE}" managed yes >/dev/null 2>&1 || true
            nmcli connection modify "${HOTSPOT_CONNECTION}" connection.autoconnect "${LEGACY_AUTOCONNECT}" >/dev/null 2>&1 || true
            if ((LEGACY_PROFILE_ACTIVE == 1)); then
                nmcli connection up "${HOTSPOT_CONNECTION}" ifname "${HOTSPOT_INTERFACE}" >/dev/null 2>&1 || true
            fi
            systemctl start lazertag-firewall.service >/dev/null 2>&1 || true
        fi
    fi
    rm -rf -- "${TRANSACTION_DIR}"
    WIFI_PASSWORD=''
    WPA_PSK=''
    LEGACY_STORED_PSK=''
    exit "${status}"
}
trap rollback EXIT

SWITCH_STARTED=1
systemctl stop lazertag-dnsmasq.service lazertag-hostapd.service lazertag-hotspot.service 2>/dev/null || true
install -o root -g root -m 0600 "${HOSTAPD_NEW}" "${HOSTAPD_CONFIG}"
install -o root -g root -m 0644 "${DNSMASQ_NEW}" "${DNSMASQ_CONFIG}"
install -o root -g root -m 0644 "${UNMANAGED_NEW}" "${UNMANAGED_CONFIG}"

if ((LEGACY_PROFILE_PRESENT == 1)); then
    # Keep a secure, dormant disaster-recovery profile. Never roll back to the
    # temporary mixed WPA/WPA2 diagnostic setting that rejected the ESP32.
    nmcli connection modify "${HOTSPOT_CONNECTION}" \
        802-11-wireless-security.key-mgmt wpa-psk \
        802-11-wireless-security.proto rsn \
        802-11-wireless-security.pairwise ccmp \
        802-11-wireless-security.group ccmp \
        802-11-wireless-security.pmf 1
    nmcli connection modify "${HOTSPOT_CONNECTION}" connection.autoconnect no
    nmcli connection down "${HOTSPOT_CONNECTION}" >/dev/null 2>&1 || true
fi
nmcli general reload conf >/dev/null 2>&1 || true
nmcli device set "${HOTSPOT_INTERFACE}" managed no

systemctl enable lazertag-hotspot.service lazertag-hostapd.service lazertag-dnsmasq.service >/dev/null
systemctl start lazertag-hotspot.service
systemctl start lazertag-hostapd.service
systemctl start lazertag-dnsmasq.service

/usr/local/libexec/lazertag/hotspot-check --wait 30 --quiet
SUCCESS=1
if ((LEGACY_PROFILE_PRESENT == 1)); then
    if ! nmcli connection delete "${HOTSPOT_CONNECTION}" >/dev/null; then
        printf 'WARNING: the disabled legacy NetworkManager hotspot profile could not be removed.\n' >&2
    fi
fi

printf 'Secured WPA2-only hotspot %s is active on %s at 192.168.4.1/24.\n' \
    "${HOTSPOT_SSID}" "${HOTSPOT_INTERFACE}"
printf 'DHCP range is %s; ESP32 relay address 192.168.4.100 is excluded.\n' \
    "${HOTSPOT_DHCP_RANGE}"
printf 'NetworkManager no longer controls the arena radio; Ethernet management remains enabled.\n'
