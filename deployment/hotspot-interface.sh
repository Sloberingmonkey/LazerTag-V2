#!/usr/bin/env bash
set -Eeuo pipefail

die() {
    printf 'lazertag-hotspot: %s\n' "$*" >&2
    exit 1
}

[[ ${EUID} -eq 0 ]] || die 'must run as root'
(($# == 1)) || die 'usage: hotspot-interface up|down'
[[ $1 == up || $1 == down ]] || die 'usage: hotspot-interface up|down'

CONFIG_FILE=${LAZERTAG_HOTSPOT_CONFIG:-/etc/lazertag/hotspot.conf}
[[ -r ${CONFIG_FILE} ]] || die "cannot read hotspot config: ${CONFIG_FILE}"
# shellcheck disable=SC1090
source "${CONFIG_FILE}"

: "${HOTSPOT_INTERFACE:?missing HOTSPOT_INTERFACE}"
: "${HOTSPOT_ADDRESS:?missing HOTSPOT_ADDRESS}"
[[ ${HOTSPOT_INTERFACE} =~ ^[a-zA-Z0-9_.:-]+$ ]] || die 'invalid hotspot interface name'
[[ ${HOTSPOT_ADDRESS} == '192.168.4.1/24' ]] || die 'unexpected hotspot address'
ip link show dev "${HOTSPOT_INTERFACE}" >/dev/null 2>&1 || die "interface not found: ${HOTSPOT_INTERFACE}"

if [[ $1 == down ]]; then
    ip address flush dev "${HOTSPOT_INTERFACE}" scope global || true
    ip -6 address flush dev "${HOTSPOT_INTERFACE}" scope link || true
    ip link set dev "${HOTSPOT_INTERFACE}" down || true
    exit 0
fi

rfkill unblock wifi
if command -v nmcli >/dev/null; then
    nmcli device set "${HOTSPOT_INTERFACE}" managed no >/dev/null 2>&1 || true
fi
ip link set dev "${HOTSPOT_INTERFACE}" down
ip address flush dev "${HOTSPOT_INTERFACE}" scope global || true
ip -6 address flush dev "${HOTSPOT_INTERFACE}" scope link || true
ip link set dev "${HOTSPOT_INTERFACE}" up
iw dev "${HOTSPOT_INTERFACE}" set power_save off
ip address replace "${HOTSPOT_ADDRESS}" dev "${HOTSPOT_INTERFACE}"
ADDRESS_STATUS=$(ip -4 -o address show dev "${HOTSPOT_INTERFACE}") \
    || die "could not read ${HOTSPOT_INTERFACE} IPv4 state"
[[ ${ADDRESS_STATUS} == *' 192.168.4.1/24 '* ]] \
    || die "${HOTSPOT_INTERFACE} did not acquire 192.168.4.1/24"
