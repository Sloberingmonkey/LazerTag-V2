#!/usr/bin/env bash
set -Eeuo pipefail

die() {
    printf 'lazertag-firewall: %s\n' "$*" >&2
    exit 1
}

[[ ${EUID} -eq 0 ]] || die 'must run as root'
command -v nft >/dev/null || die 'nftables is required'

RULES_FILE=${LAZERTAG_FIREWALL_RULES:-/etc/lazertag/lazertag-firewall.nft}
[[ -r ${RULES_FILE} ]] || die "cannot read rules: ${RULES_FILE}"

# nft batches are atomic. On reload, delete and recreate our table in the same
# transaction so a syntax error leaves the prior rules in place.
TRANSACTION=$(mktemp)
trap 'rm -f -- "${TRANSACTION}"' EXIT
if nft list table inet lazertag >/dev/null 2>&1; then
    printf 'delete table inet lazertag\n' > "${TRANSACTION}"
fi
cat "${RULES_FILE}" >> "${TRANSACTION}"
nft --check --file "${TRANSACTION}"
nft --file "${TRANSACTION}"

nft list table inet lazertag >/dev/null
