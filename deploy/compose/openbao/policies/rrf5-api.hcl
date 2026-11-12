# rrf5-api: what the HTTP API may do in OpenBao (least privilege).
# - signs access tokens with the non-exportable ECDSA key and reads its public keys (ADR-013);
# - seals new A1/CSC envelopes: asks for a wrapped data key and creates the KV entry
#   once (cas=0), and removes an entry it just created when the database write fails;
# - never decrypts: no transit/decrypt and no KV read, so the API process cannot open
#   an A1 or a CSC (ADR-004: decryption only in the authorized worker).

path "transit/sign/rrf5-token-signing/sha2-256" {
  capabilities = ["update"]
}

path "transit/keys/rrf5-token-signing" {
  capabilities = ["read"]
}

path "transit/datakey/plaintext/rrf5-envelope" {
  capabilities = ["update"]
}

path "secret/data/rrf5/*" {
  capabilities = ["create"]
}

path "secret/metadata/rrf5/*" {
  capabilities = ["delete"]
}
