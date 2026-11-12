#!/bin/sh
# Hardened local OpenBao bootstrap (run by scripts/local/openbao-hardened.ps1).
#
# First run: initializes the server (1 key share, local only), unseals it, enables
# transit and KV v2, creates the non-exportable keys, loads the policies from
# /rrf5/policies, enables AppRole with one role per process (rrf5-api, rrf5-worker,
# rrf5-key-admin), writes each role's credentials to /out and REVOKES the root
# token. Later runs only unseal.
#
# /out is a directory outside Git (C:\Users\<user>\rrf5-private\openbao-local).
# It holds the unseal key and the AppRole credentials of this local server only.
set -eu

: "${BAO_ADDR:?BAO_ADDR is required}"
OUT=/out
umask 077

initialized() { bao status -format=json 2>/dev/null | grep -q '"initialized": true'; }
sealed() { bao status -format=json 2>/dev/null | grep -q '"sealed": true'; }

for i in $(seq 1 30); do
  bao status >/dev/null 2>&1 && break
  # exit code 2 means "sealed", which is a valid answer too
  [ "$(bao status >/dev/null 2>&1; echo $?)" = "2" ] && break
  sleep 1
done

if ! initialized; then
  bao operator init -key-shares=1 -key-threshold=1 > "$OUT/init.txt"
  sed -n 's/^Unseal Key 1: //p' "$OUT/init.txt" > "$OUT/unseal-key"
  sed -n 's/^Initial Root Token: //p' "$OUT/init.txt" > "$OUT/root-token"
  rm -f "$OUT/init.txt"
  echo "initialized"
fi

if sealed; then
  bao operator unseal "$(cat "$OUT/unseal-key")" >/dev/null
  echo "unsealed"
fi

if [ -f "$OUT/bootstrap.done" ]; then
  echo "already bootstrapped"
  exit 0
fi

[ -s "$OUT/root-token" ] || { echo "no root token: regenerate one with 'bao operator generate-root' to bootstrap again" >&2; exit 1; }
export BAO_TOKEN="$(cat "$OUT/root-token")"

bao secrets list -format=json | grep -q '"transit/"' || bao secrets enable transit
bao secrets list -format=json | grep -q '"secret/"' || bao secrets enable -path=secret kv-v2
bao read transit/keys/rrf5-token-signing >/dev/null 2>&1 || bao write -f transit/keys/rrf5-token-signing type=ecdsa-p256 >/dev/null
bao read transit/keys/rrf5-envelope >/dev/null 2>&1 || bao write -f transit/keys/rrf5-envelope type=aes256-gcm96 >/dev/null

for policy in rrf5-api rrf5-worker rrf5-key-admin; do
  bao policy write "$policy" "/rrf5/policies/$policy.hcl"
done

bao auth list -format=json | grep -q '"approle/"' || bao auth enable approle
for role in rrf5-api rrf5-worker rrf5-key-admin; do
  bao write "auth/approle/role/$role" token_policies="$role" token_ttl=1h token_max_ttl=4h secret_id_ttl=0 token_type=service >/dev/null
  {
    echo "OpenBao__RoleId=$(bao read -field=role_id "auth/approle/role/$role/role-id")"
    echo "OpenBao__SecretId=$(bao write -f -field=secret_id "auth/approle/role/$role/secret-id")"
  } > "$OUT/$role.env"
done

# The root token is not kept: regenerate one with the unseal key when needed.
bao token revoke -self
rm -f "$OUT/root-token"
date -u +%Y-%m-%dT%H:%M:%SZ > "$OUT/bootstrap.done"
echo "bootstrapped; root token revoked"
