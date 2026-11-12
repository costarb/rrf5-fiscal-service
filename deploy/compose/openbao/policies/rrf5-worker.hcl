# rrf5-worker: opens A1/CSC envelopes to sign fiscal documents (ADR-004).
# Reads the KV entry and unwraps its data key. It cannot sign access tokens,
# create or delete secrets, or ask for new data keys.

path "secret/data/rrf5/*" {
  capabilities = ["read"]
}

path "transit/decrypt/rrf5-envelope" {
  capabilities = ["update"]
}
