# rrf5-key-admin: operator role for key rotation only (token signing and envelope keys).
# It can rotate and raise the minimum decryption version, never export a key,
# read a secret, decrypt or sign.

path "transit/keys/rrf5-token-signing/rotate" {
  capabilities = ["update"]
}

path "transit/keys/rrf5-envelope/rotate" {
  capabilities = ["update"]
}

path "transit/keys/rrf5-token-signing/config" {
  capabilities = ["update"]
}

path "transit/keys/rrf5-envelope/config" {
  capabilities = ["update"]
}

path "transit/keys/rrf5-token-signing" {
  capabilities = ["read"]
}

path "transit/keys/rrf5-envelope" {
  capabilities = ["read"]
}
