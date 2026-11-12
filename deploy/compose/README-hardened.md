# Hardened local stack

The vault and the bucket configured the way production expects them, on one machine.
`docker-compose.yml` stays the **test** stack: OpenBao in dev mode with a fixed root
token, because the test suite needs a privileged token to build its fixtures.

| | Test stack (`docker-compose.yml`) | Hardened stack (`docker-compose.hardened.yml`) |
| --- | --- | --- |
| OpenBao | dev mode, in memory, root token `local-dev-root` | server mode, raft storage, sealed on every start, root token revoked after bootstrap |
| Vault login | static token | AppRole, one role and one policy per process |
| SeaweedFS | no identities (anything is accepted) | S3 identities, anonymous access refused |
| Ports | 58200 (vault), 58333 (S3) | 58201 (vault), 58334 (S3) |

## Use

```powershell
.\scripts\local\hardened-stack.ps1 -Action Up        # first run: identities, init, unseal, keys, policies, AppRole, bucket
.\scripts\local\hardened-stack.ps1 -Action Verify    # vault allow/deny matrix + HardenedStackTests
.\scripts\local\hardened-stack.ps1 -Action Run -Process Api      # API with its AppRole and storage identity
.\scripts\local\hardened-stack.ps1 -Action Down      # stop, keep data (it restarts sealed; Up unseals)
.\scripts\local\hardened-stack.ps1 -Action Down -Reset           # delete volumes and generated credentials
```

Generated credentials go to `-PrivateDir` (default `%USERPROFILE%\rrf5-private`, outside
Git) and are never printed:

- `openbao-local\unseal-key`: the single unseal key (1 share, local only);
- `openbao-local\rrf5-api.env`, `rrf5-worker.env`, `rrf5-key-admin.env`: AppRole credentials;
- `seaweedfs-local\s3.json` and `storage-admin.env`, `storage-worker.env`, `storage-api.env`.

## Who can do what

**OpenBao** (`openbao/policies`, checked by `openbao/verify-policies.sh`):

| Role | Allowed | Denied |
| --- | --- | --- |
| `rrf5-api` | sign tokens, read the signing public keys, get a wrapped data key, create an envelope once, delete an envelope it just created | read an envelope, decrypt, overwrite, export or rotate keys, read policies |
| `rrf5-worker` | read an envelope, decrypt its data key | sign tokens, get data keys, write or delete envelopes |
| `rrf5-key-admin` | rotate and configure the two transit keys, read their metadata | read envelopes, decrypt, sign, export |

The API process therefore cannot open an A1 or a CSC; only the worker can (ADR-004).

**SeaweedFS** (`s3.json`, checked by `HardenedStackTests`):

| Identity | Allowed | Denied |
| --- | --- | --- |
| `rrf5-admin` | create buckets (bootstrap only, `ensure-bucket`) | delete or shorten a COMPLIANCE version |
| `rrf5-worker` | read, list and write `rrf5-regulatory` | other buckets, creating buckets, deleting a locked version |
| `rrf5-api` | read `rrf5-regulatory` | other buckets, creating buckets, deleting a locked version, changing retention |
| anonymous | nothing | everything |

SeaweedFS 3.97 refuses `GetObject` to an identity without `Write`, so `rrf5-api` holds
`Write` on the artifact bucket. The API code never writes there. A write could not change
what is read either, because:

- every read is pinned to the object version recorded in `regulatory_artifact` and is hash-verified;
- COMPLIANCE versions cannot be deleted, not even by the admin.

The cloud bucket policy must grant the API read only.

## What this does not cover yet (cloud deployment)

- TLS on the vault listener and on S3. Locally both are bound to 127.0.0.1.
- Auto-unseal through the cloud KMS instead of a local unseal key, and more than one key share.
- Platform identity (workload identity / instance role) instead of AppRole secret IDs. Locally the secret IDs never expire.
- Bucket policy and KMS key policy in the provider IAM, with separate credentials for backups (TM-15).
