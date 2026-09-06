# Mutation testing, and what it found

**Measured 2026-09-05, revisited 2026-09-06.** Stryker.NET 4.16.0 over `VelaCommerce.Domain`,
driven by the domain suite — 202 tests when this was first written, 206 now.
**Read the second-round section before quoting the score**: it moved without a test changing, and
the reason matters more than the number. Configuration in [`stryker-config.json`](../../stryker-config.json); the run takes
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

## A second round, and what it says about the number

**Measured 2026-09-06.** The score moved from 71.61% to **73.55%** across a commit that added a
static array to `OrderStateMachine` and changed nothing else in the domain — and nothing at all in
the domain test project. Six mutants in six other files flipped from Survived to Killed.

**Not one of them was killed by a test.** The first was `quantity <= 0` widened to `quantity < 0` in
`Cart.AddItem`; applying it by hand left all 202 tests green. The tool was reporting a kill for a
change nothing in the suite could detect. Re-running Stryker at the previous commit reproduced
71.61%, and re-running at the new one reproduced 73.55% twice, so it is deterministic per tree — the
cause is Stryker's per-test coverage selection shifting when the assembly's layout changes, not
anything about the tests.

The honest reading is that **the lower number was the more accurate one**, and that a mutation score
carries a component of tool artefact that no amount of care in the test suite removes.

Chasing the six down was still worth it. Three were real gaps:

| Survived mutation | What it means | Fixed |
|---|---|---|
| `new StockReservation(…, 0, …)` accepted | The ledger refused a zero-unit hold; the row recording one did not. A reservation for nothing could be written, and no ledger would ever move for it | A test asserting zero is refused |
| `description?.Trim() ?? string.Empty` → `string.Empty` | Every product's description silently empty — 288 blank product pages, nothing failing, nothing logged | A test asserting the description survives construction |
| `name?.Trim() ?? string.Empty` → `string.Empty` | The same one level down, on variant names. Both defaults are real and worth keeping, which is exactly why nothing distinguished "absent, so empty" from "always empty" | A test asserting a named variant keeps its name and an unnamed one falls back |

The other three are **unkillable by any single-mutant tool, and should stay alive**:

- **`Cart.AddItem` and `CartLine`'s constructor shield each other.** Break either guard alone and
  zero is still refused — by the other one, with the same exception type. Only breaking *both* lets
  a zero-quantity line exist. Verified by hand in both directions: each mutation alone leaves the
  whole suite green.
- **`OrderLine`'s guard is unreachable.** It is `internal`, and `Order.FromCart` is its only caller,
  building it out of cart lines that are already at least one.

Killing those three would mean making a constructor public or reaching it by reflection — testing an
arrangement the application does not have, so that a number goes up. Redundant validation is worth
having where money and stock meet, and a score that punishes it is measuring the tool's reach rather
than the code's safety.

### The part that settles it

Fixing the three real gaps moved the score from 73.55% to **73.87%** — and the report says exactly
one mutant changed status:

| | before | after |
|---|---|---|
| Killed | 228 | **229** |
| Survived | 14 | 14 |
| No coverage | 68 | 67 |

`StockReservation.cs:16` went from *NoCoverage* to *Killed*, which is the reservation guard the new
test reached for the first time. **The other two fixes moved nothing at all, because the tool was
already reporting those mutants as killed.** Writing a test that genuinely kills a mutant Stryker
had already miscounted as dead produces no change in the score whatsoever.

So the number cannot distinguish a real kill from a false one, in either direction: it rose by two
points when nothing improved, and it barely moved when three things did.

**What this changes about how the number is used here.** It was already described as a floor to hold
rather than an achievement to claim. This is the evidence for that: the floor stays at 70, and the
figure quoted elsewhere in this repository is the one this tool currently reports, with this section
as the caveat attached to it. The survivor list is still worth reading every time — it is what found
the money-overflow gap in the first round and the blank-description gap in this one. The percentage
on top of it is worth much less than it looks.

## Running it

```bash
dotnet tool restore
dotnet tool run dotnet-stryker
```

The HTML report lands in `artifacts/stryker/reports/`. CI fails the build below a score of 70 —
set under the measured 71.61 with room for ordinary movement, and meant to be raised deliberately
rather than lowered quietly, the same rule as the coverage floor.
