# Payment simulator scenarios

Vela Commerce ships its own payment gateway. `SimulatedPaymentGateway` is the default
implementation of `IPaymentGateway`, so a fresh clone completes a real purchase — signed
settlement notifications included — with no third-party account, no API key and no network call.

It is deterministic. The scenario is a pure function of the request, so the same checkout produces
the same outcome, the same gateway reference, the same event ids and the same signature bytes on
every machine and every run. Nothing here rolls dice.

## Triggering a scenario

Two ways, checked in this order:

1. **By hint** — the checkout request carries a scenario name (case-insensitive) which arrives at
   the gateway as `PaymentAuthorizationRequest.ScenarioHint`. This is what the Demo Lab's
   per-scenario permalinks use.
2. **By amount** — the last two minor units of the **order total** (subtotal + shipping + tax), so
   `$1.01`, `$47.01` and `$1,203.01` all decline. This is the fallback for a plain HTTP client, a
   Bruno request, or a shopper who just wants to put the right thing in their cart.

Anything else succeeds.

<!-- Generated from PaymentScenarioCatalog.ToMarkdownTable(). Edit the descriptors, not this table. -->

| Scenario | Trigger by hint | Trigger by amount | Authorization result | Webhooks | What it demonstrates |
|---|---|---|---|---|---|
| `Succeed` | `Succeed` | any other total | Succeeded — full amount captured in the response | none | The happy path. The order is marked paid inside the checkout request. |
| `Decline` | `Decline` | total ends in `.01` | Declined — reason `DoNotHonor` | none | A refused card is a business answer, not an exception. The reservation is released and the cart survives. |
| `Abandon` | `Abandon` | total ends in `.02` | Abandoned — nothing taken | none | Nobody said no, so nothing is retried. The reservation is left to lapse on its TTL. |
| `Duplicate` | `Duplicate` | total ends in `.03` | PendingSettlement | 2 x `payment.succeeded` — identical event id, identical signature | Exactly-once from at-least-once delivery: the second insert loses on the event-id unique index. |
| `Delay` | `Delay` | total ends in `.04` | PendingSettlement | 1 x `payment.succeeded`, after `SettlementDelay` | The ordinary asynchronous path. The UI must say "confirming payment" rather than spin. |
| `Reorder` | `Reorder` | total ends in `.05` | PendingSettlement | `payment.succeeded` (raised 2nd) delivered first, `payment.authorized` (raised 1st) after `SettlementDelay` | Out-of-order delivery is resolved by the order state machine refusing backwards edges, not by arrival order. |

### The tradeoff in the amount trigger

A genuine order total ending in `.03` will duplicate its webhook. That is accepted rather than
worked around: this is a demo whose stated purpose includes showing a duplicate webhook being
handled correctly, and a shopper cannot tell the difference because the correct handling is
invisible. Set `Payments:Simulator:RecogniseMagicAmounts` to `false` to make the explicit hint the
only trigger.

## The signature

Settlement notifications are signed with HMAC-SHA256 by `PaymentSignature`, the same type the
webhook receiver verifies with — one implementation, so the two halves cannot drift apart.

```
X-Vela-Signature: t=1772668800,v1=<64 lowercase hex characters>
```

The signed message is `{unix-seconds}.{raw-payload-bytes}`. Binding the timestamp into the MAC is
what makes the replay window enforceable: a signature lifted from a log cannot be re-dated, because
changing `t` invalidates the hash. The `v1` label is what makes the scheme replaceable — a future
`v2` can be sent alongside it and receivers migrated one at a time.

Two rules for anyone consuming this:

- **Verify over the raw request body.** Deserializing and re-serializing produces different bytes
  and a signature that fails for no security reason at all.
- **Never compare signatures with `==`.** Use `PaymentSignature.FixedTimeEquals`, which wraps
  `CryptographicOperations.FixedTimeEquals`. `string.Equals` returns on the first differing
  character, which over enough requests leaks a valid signature one character at a time.

`PaymentSignature.Verify` returns `PaymentSignatureResult` — `Valid`, `Malformed`, `Expired` or
`Mismatched` — rather than a bool, because a webhook endpoint needs to tell a replay from a forgery
from a typo when it decides what status code to send back.

## Configuration

Every key is optional. A host with no configuration at all gets a working gateway.

| Key (under `Payments:Simulator`) | Default | Notes |
|---|---|---|
| `SigningSecret` | a committed development value | Refused outside Development. Never logged — the options record redacts it in `ToString`. |
| `GatewayReferencePrefix` | `sim` | Makes a simulated reference identifiable at a glance in a log. |
| `SettlementDelay` | `00:00:03` | How long a deferred settlement waits. Must be shorter than `SignatureTolerance`. |
| `SignatureTolerance` | `00:05:00` | The replay window, in both directions. |
| `RecogniseMagicAmounts` | `true` | Whether the total may select a scenario. |

**Production.** Supply `SigningSecret` from an environment variable or a key vault reference —
never from `appsettings.Production.json`, which ships inside the container image. The committed
default is public in this repository, so anyone who has read it could otherwise forge a settlement
and mark an order paid; `AddPaymentSimulator` refuses to start outside Development while that
default is still in place.

## Wiring

```csharp
builder.Services.AddPaymentSimulator(builder.Configuration);
```

One instance behind three registrations: `IPaymentGateway` for the domain, `IPaymentSimulator` for
the checkout handler and outbox worker that need the settlement plan, and the concrete
`SimulatedPaymentGateway` for tests.

`AuthorizeAsync` returns the domain result only. The signed notifications come back from
`IPaymentSimulator.Simulate`, which returns them rather than delivering them — the order row is not
committed yet when a payment is authorized, so posting a webhook there would race the insert and
deliver a settlement for an order that does not exist. The caller enqueues the plan in the same
`SaveChangesAsync` as the order.
