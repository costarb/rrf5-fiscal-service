#!/bin/sh
# Verifies the hardened OpenBao: AppRole logins and the allow/deny matrix of each
# role, with synthetic data only (a throwaway KV path, a test data key). Prints one
# line per check and exits 1 if any check fails. Reads credentials from /out.
set -u
: "${BAO_ADDR:?BAO_ADDR is required}"
OUT=/out
FAIL=0
PROBE="rrf5/00000000-0000-0000-0000-000000000000/verify/$(date +%s)"

login() {
  . "$OUT/$1.env"
  bao write -field=token auth/approle/login role_id="$OpenBao__RoleId" secret_id="$OpenBao__SecretId"
}

# expect <role-token> <allow|deny> <label> <bao args...>
expect() {
  token=$1; want=$2; label=$3; shift 3
  if BAO_TOKEN=$token bao "$@" >/dev/null 2>&1; then got=allow; else got=deny; fi
  if [ "$got" = "$want" ]; then echo "[PASS] $label ($want)"; else echo "[FAIL] $label: expected $want, got $got"; FAIL=1; fi
}

if bao status -format=json | grep -q '"sealed": true'; then echo "[FAIL] server is sealed"; exit 1; fi

API=$(login rrf5-api) || { echo "[FAIL] rrf5-api AppRole login"; exit 1; }
WORKER=$(login rrf5-worker) || { echo "[FAIL] rrf5-worker AppRole login"; exit 1; }
KEYS=$(login rrf5-key-admin) || { echo "[FAIL] rrf5-key-admin AppRole login"; exit 1; }
echo "[PASS] AppRole login for rrf5-api, rrf5-worker and rrf5-key-admin"

INPUT=$(printf 'rrf5-verify' | base64)
expect "$API" allow "api signs a token" write transit/sign/rrf5-token-signing/sha2-256 input="$INPUT"
expect "$API" allow "api reads the signing public keys" read transit/keys/rrf5-token-signing
expect "$API" allow "api asks for a wrapped data key" write transit/datakey/plaintext/rrf5-envelope bits=256
expect "$API" allow "api creates an envelope once" kv put -mount=secret -cas=0 "$PROBE" probe=synthetic
expect "$API" deny  "api overwrites an envelope" kv put -mount=secret "$PROBE" probe=changed
expect "$API" deny  "api reads an envelope" kv get -mount=secret "$PROBE"
WRAPPED=$(BAO_TOKEN=$API bao write -field=ciphertext transit/datakey/wrapped/rrf5-envelope bits=256 2>/dev/null || BAO_TOKEN=$API bao write -field=ciphertext transit/datakey/plaintext/rrf5-envelope bits=256)
expect "$API" deny  "api decrypts a data key" write transit/decrypt/rrf5-envelope ciphertext="$WRAPPED"
expect "$API" deny  "api exports a key" read transit/export/signing-key/rrf5-token-signing
expect "$API" deny  "api rotates a key" write -f transit/keys/rrf5-token-signing/rotate
expect "$API" deny  "api reads policies" policy read rrf5-api

expect "$WORKER" allow "worker reads an envelope" kv get -mount=secret "$PROBE"
expect "$WORKER" allow "worker decrypts a data key" write transit/decrypt/rrf5-envelope ciphertext="$WRAPPED"
expect "$WORKER" deny  "worker signs a token" write transit/sign/rrf5-token-signing/sha2-256 input="$INPUT"
expect "$WORKER" deny  "worker asks for a data key" write transit/datakey/plaintext/rrf5-envelope bits=256
expect "$WORKER" deny  "worker writes an envelope" kv put -mount=secret "$PROBE-w" probe=synthetic
expect "$WORKER" deny  "worker deletes an envelope" kv metadata delete -mount=secret "$PROBE"

expect "$KEYS" deny  "key-admin reads an envelope" kv get -mount=secret "$PROBE"
expect "$KEYS" deny  "key-admin decrypts" write transit/decrypt/rrf5-envelope ciphertext="$WRAPPED"
expect "$KEYS" deny  "key-admin signs a token" write transit/sign/rrf5-token-signing/sha2-256 input="$INPUT"
expect "$KEYS" deny  "key-admin exports a key" read transit/export/encryption-key/rrf5-envelope
expect "$KEYS" allow "key-admin reads key metadata" read transit/keys/rrf5-envelope

expect "$API" allow "api removes the envelope it created" kv metadata delete -mount=secret "$PROBE"
expect "" deny "anonymous reads the signing keys" read transit/keys/rrf5-token-signing

for t in "$API" "$WORKER" "$KEYS"; do BAO_TOKEN=$t bao token revoke -self >/dev/null 2>&1; done
[ "$FAIL" = 0 ] && echo "ALL PASS" || echo "SOME CHECKS FAILED"
exit $FAIL
