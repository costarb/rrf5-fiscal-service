# Uncertain authorization result POC

This deterministic simulation proves that an authorization timeout never leads
directly to retransmission. It first consults using the original access-key and
artifact-hash context.

```powershell
npm test
npm start
```

The default is fail-closed: `NOT_FOUND`, unavailable or processing consultation
results remain `RESULT_UNKNOWN`. A retransmission requires both a `NOT_FOUND`
consultation and an explicitly injected approval policy, and is capped by a
small configured budget. That policy is deliberately outside this POC because
its fiscal and operational conditions must come from current official guidance
and approved configuration.

The simulation never labels technical retry as fiscal contingency and never
regenerates the access key or artifact.
