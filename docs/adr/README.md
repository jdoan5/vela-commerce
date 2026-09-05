# Architecture decision records

Short records of decisions that a reviewer is likely to read as a mistake, written so the reasoning
can be judged rather than guessed at. Each one names the alternative it rejected and the file or
test that now enforces it, so a decision that quietly stops being true fails CI instead of aging
into a lie in a document.

0005 to 0009 were promoted from comment blocks that already carried the argument, so the reasoning
is older than the record. Writing them up meant checking every claim those comments made, and five
did not survive: they are named inside the records rather than tidied away, because a decision
justified by something that turned out not to exist is a more useful thing to read than one that
was retrofitted to look clean.

They are numbered in the order they were written, not in order of importance, and they are never
edited to match a later change of mind: a superseded record keeps its text and gains a note saying
which record replaced it.

| # | Decision | Status |
|---|---|---|
| [0001](0001-a-demo-admin-with-no-password.md) | A demo admin with no password | Accepted |
| [0002](0002-a-per-session-price-overlay.md) | A per-session price overlay, never a write to the catalog | Accepted · addendum 2026-09-05 |
| [0003](0003-static-ssr-beside-a-webassembly-shop.md) | Static SSR for the admin, beside a WebAssembly shop | Accepted |
| [0004](0004-the-admin-cannot-ship-or-restock.md) | The admin cannot ship an order or adjust stock | Accepted |
| [0005](0005-where-the-gateway-call-sits.md) | Where the payment gateway call sits, relative to the transaction | Accepted |
| [0006](0006-the-database-decides-races.md) | Races are decided by the database, never by a SELECT that ran first | Accepted |
| [0007](0007-the-tenancy-filter-fails-closed.md) | The tenancy filter fails closed | Accepted |
| [0008](0008-a-payment-simulator-that-signs.md) | A payment gateway in the repository, that signs its own webhooks | Accepted |
| [0009](0009-no-log-analytics-workspace.md) | No Log Analytics workspace | Accepted, with corrections |
