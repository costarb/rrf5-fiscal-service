# OpenBao server for the hardened local stack (not dev mode): persistent integrated
# storage, sealed on every start, no fixed root token. Local only: the listener is
# published on 127.0.0.1 by compose. TLS is required in any shared or cloud
# deployment (see deploy/compose/README-hardened.md).

storage "raft" {
  path    = "/openbao/file"
  node_id = "rrf5-local"
}

listener "tcp" {
  address     = "0.0.0.0:8200"
  tls_disable = true
}

api_addr     = "http://127.0.0.1:8200"
cluster_addr = "http://127.0.0.1:8201"
ui           = false
