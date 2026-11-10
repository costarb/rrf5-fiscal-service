# Secret-handling evidence

**Status:** PASS FOR DISCOVERY REPOSITORY  
**Checked:** 2026-08-18

## Controls exercised

- GitHub Actions runs `gitleaks` with full history (`fetch-depth: 0`).
- `scripts/verify_secret_boundaries.py` inspects every tracked or unignored file.
- Certificate/key extensions (`pfx`, `p12`, `pem`, `key`, `cer`, `crt`, `jks`)
  are rejected from the repository candidate set.
- Private-key/PKCS#12 textual markers and tracked/unignored log files are rejected.
- `.gitignore` and `.dockerignore` are themselves checked for required secret,
  certificate, artifact and log exclusions.
- A1/CSC fixtures are not present. The normalized NFC-e fixture uses only
  synthetic identifiers and a fiscal-profile reference requiring approval.

## Executed evidence

```text
python scripts/verify_secret_boundaries.py
Secret-boundary verification: PASS
Checked tracked/unignored files plus .gitignore and .dockerignore controls.
```

The discovery scan also searched Git patch history for private-key headers and
literal password/client-secret/CSC assignments and found no match. Gitleaks in
CI remains authoritative because it has broader detectors and scans full history.

## Container boundary

There is no Dockerfile or built application image in this discovery branch, so
there are no container layers to inspect. The build-context boundary is enforced
now: `.dockerignore` excludes Git metadata, environment files, certificate/key
extensions, `secrets`, downloaded `artifacts`, logs, build outputs and private
evidence.

When a Dockerfile is introduced, T035/T036 must additionally inspect the final
image history and exported filesystem with a canary secret. Passing this current
gate is not evidence that a future Dockerfile cannot copy a secret supplied from
outside its build context.

## Runtime/log boundary

The POCs emit only candidate/runtime state, schema filenames, artifact hashes and
sanitized validation messages. They do not load A1 or CSC yet. T017/T018 must
repeat this test with disposable canary values and assert absence across stdout,
stderr, structured logs, traces, exception output and any mock SOAP capture.

## Result

Source/history controls and the future container build context are clean for the
current discovery scope. Runtime proof with actual disposable A1/CSC values and
image-layer proof remain explicit later gates; no production credential is
needed or permitted for either test.
