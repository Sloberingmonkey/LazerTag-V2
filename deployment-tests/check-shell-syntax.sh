#!/usr/bin/env bash
set -eu

if (($# == 0)); then
    root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
    set -- "${root}"/deployment/*.sh "${root}/deployment/xinitrc" "${root}"/deployment-tests/*.sh
fi

for script in "$@"; do
    bash -n -- "${script}"
done
