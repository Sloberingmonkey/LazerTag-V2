#!/usr/bin/env bash
set -Eeuo pipefail

ROOT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
TEST_DIR=$(mktemp -d)
trap 'rm -rf -- "${TEST_DIR}"' EXIT
mkdir -p "${TEST_DIR}/bin"

cat > "${TEST_DIR}/hotspot.conf" <<'EOF'
HOTSPOT_INTERFACE='wlan0'
EOF

cat > "${TEST_DIR}/bin/systemctl" <<'EOF'
#!/usr/bin/env bash
[[ $# == 3 && ${1:-} == is-active && ${2:-} == --quiet ]] || exit 1
case "${3:-}" in
    lazertag-hotspot.service|lazertag-hostapd.service|lazertag-dnsmasq.service) exit 0 ;;
    *) exit 1 ;;
esac
EOF

cat > "${TEST_DIR}/bin/ip" <<'EOF'
#!/usr/bin/env bash
printf '3: wlan0    inet %s brd 192.168.4.255 scope global wlan0\n' \
    "${HOTSPOT_TEST_ADDRESS:-192.168.4.1/24}"
EOF

cat > "${TEST_DIR}/bin/iw" <<'EOF'
#!/usr/bin/env bash
printf 'Interface wlan0\n'
printf '\ttype %s\n' "${IW_TYPE:-AP}"
printf '\tchannel 6 (2437 MHz), width: 20 MHz\n'
EOF

cat > "${TEST_DIR}/bin/hostapd_cli" <<'EOF'
#!/usr/bin/env bash
# Control-socket access is intentionally unavailable in this regression test.
# A healthy kernel AP mode must not be rolled back for this optional diagnostic.
exit 1
EOF

chmod 0755 \
    "${TEST_DIR}/bin/systemctl" \
    "${TEST_DIR}/bin/ip" \
    "${TEST_DIR}/bin/iw" \
    "${TEST_DIR}/bin/hostapd_cli"

OUTPUT=$(
    PATH="${TEST_DIR}/bin:${PATH}" \
    LAZERTAG_HOTSPOT_CONFIG="${TEST_DIR}/hotspot.conf" \
        bash "${ROOT_DIR}/deployment/hotspot-check.sh"
)

[[ ${OUTPUT} == 'Hotspot OK: WPA2 AP and DHCP are active at 192.168.4.1.' ]]

if PATH="${TEST_DIR}/bin:${PATH}" \
    IW_TYPE=AP/VLAN \
    LAZERTAG_HOTSPOT_CONFIG="${TEST_DIR}/hotspot.conf" \
        bash "${ROOT_DIR}/deployment/hotspot-check.sh" --quiet \
        >"${TEST_DIR}/not-ap.log" 2>&1; then
    printf 'hotspot-check accepted a non-AP interface\n' >&2
    exit 1
fi
grep -Fq 'Observed wireless interface state:' "${TEST_DIR}/not-ap.log"

if PATH="${TEST_DIR}/bin:${PATH}" \
    HOTSPOT_TEST_ADDRESS=192.168.4.10/24 \
    LAZERTAG_HOTSPOT_CONFIG="${TEST_DIR}/hotspot.conf" \
        bash "${ROOT_DIR}/deployment/hotspot-check.sh" --quiet \
        >"${TEST_DIR}/wrong-address.log" 2>&1; then
    printf 'hotspot-check accepted the wrong arena address\n' >&2
    exit 1
fi
grep -Fq 'Observed IPv4 state:' "${TEST_DIR}/wrong-address.log"

printf 'hotspot-check regression test passed\n'
