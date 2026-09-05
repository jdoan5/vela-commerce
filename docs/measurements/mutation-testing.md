# Mutation testing, and what it found

**Measured 2026-09-05.** Stryker.NET 4.16.0 over `VelaCommerce.Domain`, driven by the 202-test
domain suite. Configuration in [`stryker-config.json`](../../stryker-config.json); the run takes
about three minutes and gates CI at a score of 70.

This project already did mutation testing by hand — every claim in a comment was checked by breaking
the code and watching a named test go red. That works, and it is why the checkout race, the
idempotency filter and the settlement dedupe are trustworthy. What it cannot do is find the places
nobody thought to look, because a human only mutates what they are already thinking about.

## What it found

The first run scored **62.50%**: 536 mutants, 295 killed, 58 survived, 119 not covered by any test.
A survivor is a change to production code that all 387 tests were happy with.

32 of the 58 survivors were rewritten string literals — almost all `DomainException` messages —
and those are excluded now, for a reason given below. That left **26 substantive survivors**, and
they clustered:

| Survived mutation | What it means | Fixed |
|---|---|---|
| `checked(a.Amount + b.Amount)` → unchecked, on `+`, `-` and `*` | **Nothing tested money overflow.** A `long` of minor units that overflows does not throw, it wraps to a negative — a total that pays the customer | 3 tests forcing each operator past the range |
| `a.Amount < b.Amount` → `<=`, and the bodies of `>`, `<=`, `>=` deletable outright | Three of the four comparison operators were never called, and nothing compared two **equal** amounts — the only input where a comparison operator differs from the one beside it | 2 tests, all four operators, equal and unequal |
| `quantity > Reserved` → `>=` in `StockItem.Release` | Nothing released **exactly** what was reserved, which is the ordinary case: a cancelled order gives all of it back. Tightening by one would have broken the common path silently | A test releasing exactly the held quantity, and one releasing one more |
| `quantity <= 0` → `< 0` in five places | No test ever passed **zero**. Positive and negative were covered; the boundary between them was not | Tests passing zero to reserve and release |
| `onHand < 0` → `<= 0` in the `StockItem` constructor | This one corrected *me*. I wrote the test asserting zero on-hand is refused and it failed — correctly. A sold-out variant still has a row, so zero must be **accepted**, and that is what the mutant would have broken | A test asserting zero is legal and negative is not |

Eleven new tests. Score after: **71.61%** (222 killed, 20 survived, 68 uncovered).

## What is excluded, and why

**String mutations only.** Stryker rewrites string literals, which here are almost entirely
`DomainException` messages: 51 of the first run's 119 uncovered mutants and 32 of its 58 survivors.
Killing them means asserting exact exception wording, which couples a test to a sentence rather than
to a rule and turns improving that sentence into a build failure. The messages in this codebase are
meant to get sharper over time. Everything that changes **behaviour** — arithmetic, comparisons,
boundaries, conditionals, statement removal, `checked` — stays in.

Migrations are excluded because they are generated.

## What the number does not cover

**Only `VelaCommerce.Domain`, driven only by the domain suite.** That is a deliberate scoping
decision with a visible cost: `OutboxMessage` accounts for 24 of the 88 remaining survivors and
uncovered mutants, and it is genuinely exercised — by the *integration* suite, which this run does
not use. Those mutants are reported as uncovered because of how the run is scoped, not because the
code is untested.

Widening it to Infrastructure and Api driven by the integration suite is possible and would take the
run from three minutes to something much longer, because every mutant re-runs tests that each start
a PostgreSQL container. That is a real trade, not an oversight: the domain is where mutation testing
pays best, since it is pure logic with fast tests and the invariants everything else depends on.

**A mutation score is not a quality score.** 71.61% means 71.61% of the behaviour-changing edits
Stryker knows how to make were caught. It says nothing about the edits it cannot make, and a project
can raise it by writing assertions that pin implementation details rather than rules. The number is
useful as a floor that must not fall, and as a list of survivors to read — which is the part that
found the money-overflow gap.

## Running it

```bash
dotnet tool restore
dotnet tool run dotnet-stryker
```

The HTML report lands in `artifacts/stryker/reports/`. CI fails the build below a score of 70 —
set under the measured 71.61 with room for ordinary movement, and meant to be raised deliberately
rather than lowered quietly, the same rule as the coverage floor.
