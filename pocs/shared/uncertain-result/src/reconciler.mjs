export const States = Object.freeze({
  READY: "READY",
  SENDING: "SENDING",
  RESULT_UNKNOWN: "RESULT_UNKNOWN",
  CONSULTING: "CONSULTING",
  AUTHORIZED: "AUTHORIZED",
  REJECTED: "REJECTED",
});

export class TransmissionTimeout extends Error {
  constructor(message = "Authorization response was not received") {
    super(message);
    this.name = "TransmissionTimeout";
  }
}

export class UncertainResultReconciler {
  #gateway;
  #mayRetransmit;
  #maxRetransmissions;

  constructor({ gateway, mayRetransmit = () => false, maxRetransmissions = 1 }) {
    if (!gateway?.transmit || !gateway?.consult) throw new TypeError("gateway must implement transmit and consult");
    if (!Number.isInteger(maxRetransmissions) || maxRetransmissions < 0) {
      throw new TypeError("maxRetransmissions must be a non-negative integer");
    }
    this.#gateway = gateway;
    this.#mayRetransmit = mayRetransmit;
    this.#maxRetransmissions = maxRetransmissions;
  }

  async execute(document) {
    assertDocument(document);
    const audit = [];
    let retransmissions = 0;

    while (true) {
      audit.push(event(States.SENDING, retransmissions === 0 ? "TRANSMIT" : "RETRANSMIT"));
      try {
        const result = await this.#gateway.transmit(document);
        return terminalResult(result, audit, retransmissions);
      } catch (error) {
        if (!(error instanceof TransmissionTimeout)) throw error;
        audit.push(event(States.RESULT_UNKNOWN, "TRANSMISSION_TIMEOUT"));
      }

      audit.push(event(States.CONSULTING, "CONSULT_BEFORE_RETRY"));
      const consultation = await this.#gateway.consult({
        accessKey: document.accessKey,
        artifactSha256: document.artifactSha256,
      });

      if (consultation.status === States.AUTHORIZED || consultation.status === States.REJECTED) {
        return terminalResult(consultation, audit, retransmissions);
      }

      audit.push(event(States.RESULT_UNKNOWN, `CONSULT_${consultation.status}`));
      const retryContext = Object.freeze({ document, consultation, retransmissions, audit: [...audit] });
      const retryApproved = consultation.status === "NOT_FOUND"
        && retransmissions < this.#maxRetransmissions
        && await this.#mayRetransmit(retryContext);

      if (!retryApproved) {
        return { state: States.RESULT_UNKNOWN, retransmissions, audit, consultation };
      }
      retransmissions += 1;
    }
  }
}

function terminalResult(result, audit, retransmissions) {
  if (result.status !== States.AUTHORIZED && result.status !== States.REJECTED) {
    throw new TypeError(`Unexpected transmission result: ${result.status}`);
  }
  audit.push(event(result.status, "TERMINAL_RESULT"));
  return { state: result.status, retransmissions, audit, result };
}

function assertDocument(document) {
  for (const field of ["idempotencyKey", "accessKey", "artifactSha256"]) {
    if (typeof document?.[field] !== "string" || document[field].length === 0) {
      throw new TypeError(`document.${field} is required`);
    }
  }
  if (!/^[a-f0-9]{64}$/.test(document.artifactSha256)) {
    throw new TypeError("document.artifactSha256 must be a lowercase SHA-256");
  }
}

function event(state, action) {
  return Object.freeze({ sequence: null, state, action });
}

export function sequenceAudit(audit) {
  return audit.map((entry, index) => ({ ...entry, sequence: index + 1 }));
}
