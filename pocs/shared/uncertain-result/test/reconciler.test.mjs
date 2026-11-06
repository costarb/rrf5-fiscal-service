import assert from "node:assert/strict";
import test from "node:test";
import { States, TransmissionTimeout, UncertainResultReconciler } from "../src/reconciler.mjs";

const document = Object.freeze({
  idempotencyKey: "POC-UNCERTAIN-001",
  accessKey: "SYNTHETIC-ACCESS-KEY",
  artifactSha256: "a".repeat(64),
});

test("consults after timeout and does not retransmit an already authorized document", async () => {
  const calls = [];
  const gateway = {
    async transmit() { calls.push("transmit"); throw new TransmissionTimeout(); },
    async consult() { calls.push("consult"); return { status: States.AUTHORIZED }; },
  };
  const result = await new UncertainResultReconciler({ gateway }).execute(document);
  assert.equal(result.state, States.AUTHORIZED);
  assert.equal(result.retransmissions, 0);
  assert.deepEqual(calls, ["transmit", "consult"]);
});

test("fails closed when consultation is inconclusive", async () => {
  const calls = [];
  const gateway = {
    async transmit() { calls.push("transmit"); throw new TransmissionTimeout(); },
    async consult() { calls.push("consult"); return { status: "UNAVAILABLE" }; },
  };
  const result = await new UncertainResultReconciler({ gateway, mayRetransmit: () => true }).execute(document);
  assert.equal(result.state, States.RESULT_UNKNOWN);
  assert.deepEqual(calls, ["transmit", "consult"]);
});

test("requires explicit approval and consultation before one retransmission", async () => {
  const calls = [];
  let sendCount = 0;
  const gateway = {
    async transmit() {
      calls.push("transmit");
      sendCount += 1;
      if (sendCount === 1) throw new TransmissionTimeout();
      return { status: States.AUTHORIZED };
    },
    async consult() { calls.push("consult"); return { status: "NOT_FOUND" }; },
  };
  const result = await new UncertainResultReconciler({
    gateway,
    mayRetransmit: ({ consultation }) => consultation.status === "NOT_FOUND",
  }).execute(document);
  assert.equal(result.state, States.AUTHORIZED);
  assert.equal(result.retransmissions, 1);
  assert.deepEqual(calls, ["transmit", "consult", "transmit"]);
});

test("does not retransmit NOT_FOUND by default", async () => {
  let transmissions = 0;
  const gateway = {
    async transmit() { transmissions += 1; throw new TransmissionTimeout(); },
    async consult() { return { status: "NOT_FOUND" }; },
  };
  const result = await new UncertainResultReconciler({ gateway }).execute(document);
  assert.equal(result.state, States.RESULT_UNKNOWN);
  assert.equal(transmissions, 1);
});
