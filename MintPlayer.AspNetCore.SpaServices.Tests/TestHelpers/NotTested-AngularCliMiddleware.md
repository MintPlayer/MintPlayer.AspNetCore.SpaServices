# Why `AngularCliMiddleware.StartAngularCliServerAsync` has no tests

The seam exists — `StartAngularCliServerAsync` takes an optional `IProcessLauncher` and an optional
`HttpMessageHandler`, so the whole start-up handshake *can* be driven with fakes, and a full test
class was written against it: it covered the announced-URL happy path, the "any HTTP response counts
as ready" rule, the missing-`openbrowser`-group error and the script-exited-early error.

**Those tests were removed, because they intermittently crash the xunit test host.**

## What happens

Not a failure — a crash. `Test host process crashed`, the run aborts, and every result after that
point is lost, including tests in unrelated suites. Measured on `net11.0` with coverage collection:

| Run | Result |
|---|---|
| 1 | 4 passed |
| 2 | **crashed after 1** |
| 3 | 4 passed |

Roughly one run in three. Individually each test passes every time; it only appears when several run
in sequence. Without the class, the suite is stable (`463 passed`, exit 0, repeatedly).

## Why

`EventedStreamReader`'s constructor starts its read loop fire-and-forget:

```csharp
Task.Factory.StartNew(Run, CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
```

Nothing awaits it, nothing cancels it, and `EventedStreamReader` is not disposable. `Run` is an
`async Task`, so `StartNew` hands back a `Task<Task>` whose inner task is never observed.

`StartAngularCliServerAsync` creates a `NodeScriptRunner` — two of these readers — and never disposes
it. In production that is deliberate: the dev server lives as long as the application. In a test it
means every call abandons two live background loops over streams the test is about to drop, and with
several tests in a row those race the host's shutdown.

This is the same root cause as the other gap recorded in `AngularCliMiddlewareTests`' sibling note:
awaiting `WaitForMatch` twice against one reader also crashes the host, and that is a live production
path (`AngularPrerendererBuilder` does it whenever its occurrences count is 2).

## What would fix it

Give `EventedStreamReader` a cancellation token and an `IDisposable`, and have `NodeScriptRunner`
stop both readers when it is disposed. That is a real behaviour change to shipped code, so it is out
of scope for the coverage work (NFR-4.1 in `docs/PRD-Coverage-Raise.md`) and belongs in its own
change with its own tests.

Until then the seam is in place and the tests can be restored in one commit.
