# Architecture decision records

Short records of decisions that a reviewer is likely to read as a mistake, written so the reasoning
can be judged rather than guessed at. Each one names the alternative it rejected and the file or
test that now enforces it, so a decision that quietly stops being true fails CI instead of aging
into a lie in a document.

They are numbered in the order they were written, not in order of importance, and they are never
edited to match a later change of mind: a superseded record keeps its text and gains a note saying
which record replaced it.

| # | Decision | Status |
|---|---|---|
| [0001](0001-a-demo-admin-with-no-password.md) | A demo admin with no password | Accepted |
| [0002](0002-a-per-session-price-overlay.md) | A per-session price overlay, never a write to the catalog | Accepted · addendum 2026-09-05 |
| [0003](0003-static-ssr-beside-a-webassembly-shop.md) | Static SSR for the admin, beside a WebAssembly shop | Accepted |
| [0004](0004-the-admin-cannot-ship-or-restock.md) | The admin cannot ship an order or adjust stock | Accepted |
