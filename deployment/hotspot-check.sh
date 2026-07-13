#!/usr/bin/env bash
set -Eeuo pipefail

WAIT_SECONDS=0
QUIET=0

while (($#)); do
    case "$1" in
        --wait)
            (($# >= 2)) || { printf 'hotspot-check: --wait requires seconds\n' >&2; exit 2; }
            WAIT_SECONDS=$2
            shift 2
            ;;
        --quiet)
            QUIET=1
            shift
            ;;
        *)
            printf 'Usage: hotspot-check [--wait SECONDS] [--quiet]\n' >&2
            exit 2
            ;;
    esac
done

[[ ${WAIT_SECONDS} =~ ^[0-9]+$ ]] || { printf 'hotspot-check: wait must be numeric\n' >&2; exit 2; }
((WAIT_SECONDS <= 120)) || { printf 'hotspot-check: wait cannot exceed 120 seconds\n' >&2; exit 2; }

CONFIG_FILE=${LAZERTAG_HOTSPOT_CONFIG:-/etc/lazertag/hotspot.conf}
[[ -r ${CONFIG_FILE} ]] || { printf 'hotspot-check: cannot read %s\n' "${CONFIG_FILE}" >&2; exit 2; }
# shellcheck disable=SC1090
source "${CONFIG_FILE}"
: "${HOTSPOT_INTERFACE:?missing HOTSPOT_INTERFACE}"
[[ ${HOTSPOT_INTERFACE} =~ ^[a-zA-Z0-9_.:-]+$ ]] \
    || { printf 'hotspot-check: invalid interface\n' >&2; exit 2; }

deadline=$((SECONDS + WAIT_SECONDS))

hotspot_is_ready() {
    local address_status
    local wireless_status

    systemctl is-active --quiet lazertag-hotspot.service || return 1
    systemctl is-active --quiet lazertag-hostapd.service || return 1
    systemctl is-active --quiet lazertag-dnsmasq.service || return 1

    address_status=$(LC_ALL=C ip -4 -o address show dev "${HOTSPOT_INTERFACE}" 2>/dev/null) \
        || return 1
    grep -Eq \
        '^[0-9]+:[[:space:]]+[^[:space:]]+[[:space:]]+inet[[:space:]]+192\.168\.4\.1/24([[:space:]]|$)' \
        <<<"${address_status}" || return 1

    # Verify the kernel's actual wireless interface mode. hostapd_cli's control
    # socket is useful for diagnostics, but it is not a reliable activation gate
    # on every Raspberry Pi OS/systemd combination. A running hostapd process,
    # active DHCP service, exact arena address, and kernel AP mode are the
    # production readiness conditions.
    wireless_status=$(LC_ALL=C iw dev "${HOTSPOT_INTERFACE}" info 2>/dev/null) || return 1
    grep -Eq '^[[:space:]]*type[[:space:]]+AP[[:space:]]*$' <<<"${wireless_status}"
}

while :; do
    if hotspot_is_ready; then
        ((QUIET == 1)) || printf 'Hotspot OK: WPA2 AP and DHCP are active at 192.168.4.1.\n'
        exit 0
    fi
    ((SECONDS < deadline)) || break
    sleep 1
done

printf 'Hotspot health check failed.\n' >&2
printf 'Observed IPv4 state:\n' >&2
ip -4 -o address show dev "${HOTSPOT_INTERFACE}" >&2 || true
printf 'Observed wireless interface state:\n' >&2
iw dev "${HOTSPOT_INTERFACE}" info >&2 || true
printf 'Observed hostapd control state:\n' >&2
hostapd_cli -p /run/lazertag-hostapd -i "${HOTSPOT_INTERFACE}" status >&2 || true
systemctl --no-pager --full status lazertag-hotspot.service lazertag-hostapd.service lazertag-dnsmasq.service >&2 || true
exit 1
