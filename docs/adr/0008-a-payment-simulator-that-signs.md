# 0008 — A payment gateway in the repository, that signs its own webhooks

**Status:** Accepted · recorded 2026-09-05 · the decision itself predates it (Phase 4)

## Context

A shop needs a payment processor. Using Stripe test mode means anyone cloning this repository needs
an account, an API key and a network to see a purchase complete — and the demo goes dark the day a
key rotates. Using a stub that returns `Succeeded` means the webhook receiver's HMAC verification,
its constant-time comparison, its replay window and its dedupe index are never executed by anything
but a mock.

## Decision

`SimulatedPaymentGateway` is the default `IPaymentGateway`, it lives in this repository, and **it
signs its settlement notifications with real HMAC-SHA256** using the same `PaymentSignature` helper
the receiver verifies with.

That last part is the decision. A simulator that merely returns an outcome tests the happy path; one
that signs gives the receiver something real to reject. The receiver verifies the HMAC over **the
bytes that arrived** — never a re-serialisation — which is why the outbox transmits stored bytes
unchanged, and why a forged, edited or replayed settlement is turned away by the same code a real
acquirer's would meet.

It is deterministic: the same request produces the same reference ids, bytes and signatures every
time, and the gateway reference derives from the order and the idempotency key and **not** from the
amount — so a retried refund of a different amount cannot collide with an earlier one.

## Consequences

`git clone` and a local PostgreSQL are enough to watch a cart become a paid order, a duplicate
webhook be recognised, and three different forgeries be refused. No account, no API key, no network
on the money path.

The committed development signing secret is refused outside Development, and so is the Terraform
placeholder — both asserted, including that the placeholder the guard refuses is the one Terraform
actually applies.

**Where the claims outrun the code**, found while writing this and worth recording:

- `Program.cs` still says the environment flag "makes it refuse to start outside Development while
  the committed development signing secret is in use." **It does not.** `AssertUsable` refuses on the
  money path, not at startup. This is the same overstatement a previous audit corrected in eight
  places; this instance survived.
- `PaymentScenarioCatalog` claims the committed `PAYMENT-SCENARIOS.md` is generated from
  `Descriptors` and "cannot drift apart". Nothing enforces that, and it **has** drifted: the document
  lists `RecogniseMagicAmounts` as defaulting to `true` when it defaults to `false`.
- The `AssertUsable` calls on the webhook receiver and on `RefundAsync` have no test.

None of these changes the decision. All of them are the kind of thing this repository treats as a
bug, and they are recorded here rather than quietly fixed so the pattern is visible: a claim in a
comment with no test behind it drifts, and the drift is invisible until someone reads for it.

Nothing here demonstrates PCI handling, 3-D Secure, or a real acquirer's failure modes, and the
README should not imply otherwise. What it demonstrates is the shape: a port, an adapter that can be
swapped, and a receiver hardened against an adversary rather than against a mock.
