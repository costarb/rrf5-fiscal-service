import { States, TransmissionTimeout, UncertainResultReconciler, sequenceAudit } from "./reconciler.mjs";

const document = Object.freeze({
  idempotencyKey: "POC-UNCERTAIN-001",
  accessKey: "SYNTHETIC-ACCESS-KEY",
  artifactSha256: "0".repeat(64),
});

let transmissions = 0;
const gateway = {
  async transmit() {
    transmissions += 1;
    throw new TransmissionTimeout();
  },
  async consult() {
    return { status: States.AUTHORIZED, protocolRef: "SYNTHETIC-PROTOCOL" };
  },
};

const result = await new UncertainResultReconciler({ gateway }).execute(document);
process.stdout.write(`${JSON.stringify({ ...result, audit: sequenceAudit(result.audit), transmissions })}\n`);
