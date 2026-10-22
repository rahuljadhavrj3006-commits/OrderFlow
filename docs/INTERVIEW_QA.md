# Interview Q&A - Concurrency, Async, REST, Middleware, Microservices (.NET)

How to use this file:

1. **Run the demo first, then read the answer.** Every answer points at code in this repo (`ConcurrencyLab`, `Orders.Api`, `Inventory.Api`, `OrderFlow.Shared`). Being able to say *"I built and tested this"* beats a memorised definition.
2. **Answer shape:** one-line definition → why it matters → a concrete example → the trade-off. Interviewers listen for the trade-off.
3. **Part 6 (problem-solving stories)** uses the STAR shape (Situation, Task, Action, Result). The *Result* lines are templates: put in **your own real numbers and real production stories**. If you haven't hit a problem at work, say honestly that you reproduced it in your own project and show the code.

Contents: [1 Threads & async](#part-1--threads-task-and-asyncawait) · [2 Thread safety](#part-2--synchronization-and-thread-safety) · [3 REST](#part-3--rest-api-design) · [4 Middleware](#part-4--middleware-and-the-aspnet-core-pipeline) · [5 Microservices](#part-5--microservices-and-resilience) · [6 Problem-solving stories](#part-6--how-did-you-solve-it-stories) · [7 Rapid fire](#part-7--rapid-fire) · [8 Coding drills](#part-8--coding-drills)

---

## Part 1 - Threads, Task and async/await

### Q1. What is the difference between a process, a thread, a ThreadPool thread and a Task?
- **Process**: isolated memory space. **Thread**: unit of execution inside a process; ~1 MB stack by default, expensive to create.
- **ThreadPool thread**: a reused thread the runtime manages; cheap to borrow, but you give up control of its lifetime.
- **Task**: a *promise of a future result* (or completion/failure). It is **not** a thread. A `Task` from `Task.Run` runs on a pool thread; a `Task` from `Task.Delay` or an HTTP call has **no thread at all** while it waits.
- *Demo:* `dotnet run --project src/ConcurrencyLab -- threads`

### Q2. Concurrency vs parallelism vs asynchrony?
- **Concurrency**: dealing with many things at once (structure) - they may interleave on a single thread.
- **Parallelism**: *executing* many things at the same instant on multiple cores. Best for **CPU-bound** work.
- **Asynchrony**: not blocking a thread while waiting for something (usually I/O). Best for **I/O-bound** work.
- *Demo:* `-- async` shows sync sequential ≈ 2500 ms, async sequential ≈ 2500 ms (thread free, not faster), async concurrent ≈ 500 ms.

### Q3. Does `async` make code faster?
Not a single call. It lets the thread go do other work while waiting, so **throughput/scalability** improves (a server handles more concurrent requests with the same threads). Latency of one operation is unchanged; latency of *several independent operations* improves only if you start them together (`Task.WhenAll`).

### Q4. What does `await` actually do?
The compiler turns the method into a **state machine**. At an `await` on an incomplete task, the method **returns to its caller** and registers the rest of the method as a **continuation**. When the task completes, the continuation is scheduled (on the captured `SynchronizationContext`/`TaskScheduler`, or the pool). No thread is blocked in between. `async` itself does **not** create threads.

### Q5. `Task.Run` vs a truly async API - when do you use which?
- `Task.Run` = **offload CPU-bound work** to the pool (e.g., keep a UI responsive, or parallelise computation).
- Don't wrap I/O in `Task.Run` on a server ("fake async"): it still occupies a pool thread, which is exactly what async I/O avoids. Use the real async API (`HttpClient.GetAsync`, `ReadAsync`, EF `ToListAsync`).

### Q6. `Task` vs `ValueTask`?
`ValueTask<T>` avoids a heap allocation when the result is usually available **synchronously** (cache hit, buffered read). Rules: await it **once**, don't `WhenAll` it or store it; if in doubt, use `Task`. Use it on proven hot paths only - measure first.

### Q7. What does `ConfigureAwait(false)` do and when do you use it?
It says "don't resume on the captured context". In **library code** it avoids deadlocks and context-switch overhead. ASP.NET Core has **no** `SynchronizationContext`, so in app code it makes little difference; in UI apps use the default (`true`) when you need to touch UI after the await.

### Q8. Why does `.Result` / `.Wait()` deadlock?
In a context with a single-threaded `SynchronizationContext` (UI, classic ASP.NET): the caller **blocks** that thread waiting for the task; the task's continuation wants to run **on that same thread** → neither can proceed. Fixes: **go async all the way** (best), or `ConfigureAwait(false)` inside the awaited code, never block on async.
- *Demo:* `-- deadlock` reproduces it with a custom single-thread context and a 2 s timeout.

### Q9. What is thread-pool starvation? How do you recognise and fix it?
- **Cause:** all pool threads are *blocked* (`.Result`, `.Wait()`, `Thread.Sleep`, sync I/O, `lock` held across slow work) while new work queues up. The pool injects new threads slowly (~1-2/s once past the minimum).
- **Symptom:** latency spikes and timeouts under load while **CPU is low**; thread count slowly climbing.
- **Diagnose:** `dotnet-counters` (`ThreadPool Queue Length`, `ThreadPool Thread Count`), `dotnet-stack`/dump to see blocked stacks.
- **Fix:** remove blocking calls (async end-to-end), limit concurrency (`SemaphoreSlim`), avoid sync-over-async; raising `SetMinThreads` is a band-aid.
- *Demo:* `-- starvation` shows a trivial `Task.Run` waiting seconds for a thread, then ~0 ms when the same load awaits instead of blocking.

### Q10. What is `async void` and why avoid it?
Exceptions can't be caught by the caller and can crash the process; you can't await it or know when it finished. Only valid for **event handlers**. Return `Task` instead.

### Q11. What happens to exceptions with `Task.WhenAll`?
`await Task.WhenAll(...)` rethrows only the **first** exception, but all are in `whenAllTask.Exception.InnerExceptions`. Log/aggregate them if you care about every failure. An unawaited faulted task becomes an *unobserved* exception (no crash since .NET 4.5, but silent failure).
- *Demo:* `-- exceptions`

### Q12. `Task.WhenAll` vs `Task.WhenAny` vs sequential `await`?
- Sequential: total time = **sum**. `WhenAll` (start all first, then await): total ≈ **slowest**. `WhenAny`: first to finish - timeouts, "fastest replica wins" (hedged requests).
- Gotcha: `foreach (var x in items) await Foo(x)` is sequential; `items.Select(Foo)` + `WhenAll` starts them all - with 10 000 items you need throttling (Q14).
- *Code:* `OrdersService.CreateCoreAsync` reserves all order lines with `WhenAll`.

### Q13. How does cancellation work in .NET?
**Cooperative**: the caller owns a `CancellationTokenSource`, passes its `Token` down; the callee checks it (`ThrowIfCancellationRequested`) or passes it to APIs that observe it. Nothing is forcibly killed.
- Link tokens (`CreateLinkedTokenSource`) to combine "user cancelled" **or** "timeout".
- In ASP.NET Core, bind `HttpContext.RequestAborted` / a `CancellationToken` parameter so work stops when the client disconnects.
- Catch `OperationCanceledException` separately from real errors. For **cleanup/compensation** use `CancellationToken.None` (see `SafeReleaseAsync`).
- `Task.WaitAsync(timeout)` stops *waiting*, it does **not** stop the work - pass a token for that.
- *Demo:* `-- cancel`

### Q14. How do you limit concurrency? ("Call an API 10 000 times, max 5 at once")
- `Parallel.ForEachAsync(items, new ParallelOptions { MaxDegreeOfParallelism = 5 }, async (item, ct) => ...)` - shortest.
- `SemaphoreSlim(5)` with `WaitAsync` / `Release` in `finally` - flexible, works with `WhenAll`.
- `Channel<T>` + N consumer tasks - when producers and consumers run continuously.
- *Demo:* `-- throttle`

### Q15. Producer/consumer: `BlockingCollection` vs `Channel<T>`?
`BlockingCollection` blocks threads; `Channel<T>` is **async-native** (`WriteAsync`/`ReadAsync`, `ReadAllAsync` as `IAsyncEnumerable`). A **bounded** channel gives **backpressure**: when full, producers wait instead of memory growing unbounded.
- *Code:* `Orders.Api/Events.cs` (`ChannelEventPublisher` + `OrderEventProcessor : BackgroundService`). *Demo:* `-- channels`

### Q16. How do you run background work in ASP.NET Core?
`BackgroundService`/`IHostedService` (this repo), a queue + worker, or Hangfire/Quartz for scheduling. **Don't** fire-and-forget `Task.Run` inside a request: it isn't tracked, loses exceptions, and is killed on shutdown. Respect the `stoppingToken`; decide how to drain on shutdown.

### Q17. What is `IAsyncEnumerable<T>`?
Async streams: `await foreach` consumes items as they become available instead of buffering the whole result. Good for paging through a DB/API, reading channels, streaming responses.

### Q18. CPU-bound vs I/O-bound - how do you decide the approach?
I/O-bound → `async`/`await` (no thread while waiting). CPU-bound → `Parallel.For/ForEach`, PLINQ or `Task.Run`, sized to core count. Mixed pipeline → async for I/O stages, bounded parallelism for the CPU stage.

---

## Part 2 - Synchronization and thread safety

### Q19. `lock` vs `Interlocked` vs `SemaphoreSlim` vs `ReaderWriterLockSlim` vs `Monitor`?
| Tool | Use when |
|---|---|
| `Interlocked` | A single numeric variable/reference: increment, add, exchange, compare-exchange. Lock-free, fastest. |
| `lock` / `Monitor` | Protect a short critical section touching several fields. **Cannot `await` inside a `lock`.** |
| `SemaphoreSlim` | Async-friendly mutual exclusion (`new SemaphoreSlim(1,1)`) or throttling N at a time. |
| `ReaderWriterLockSlim` | Many readers, few writers, read-heavy. Often a `ConcurrentDictionary`/immutable snapshot is simpler. |
- *Demo:* `-- races` (lost updates with `counter++`: typically millions of increments short).

### Q20. Why is `counter++` not thread-safe?
It is *read → add → write*, three steps. Two threads can read the same value and both write back `n+1` → a **lost update**. Fix with `Interlocked.Increment` or a lock.

### Q21. How does the inventory service avoid overselling without a lock?
A **compare-and-swap loop**: read current stock; if `< requested` fail; otherwise `Interlocked.CompareExchange(ref stock, current - qty, current)`. If another thread changed it meanwhile, CAS fails and we re-read and retry. The invariant "stock never negative" holds under any interleaving. *Test:* `Concurrent_reservations_never_oversell` runs 2 000 parallel reservations against 500 stock and asserts exactly 500 succeed.
- *DB equivalent:* `UPDATE Stock SET Qty = Qty - @n WHERE Sku = @sku AND Qty >= @n` and check `rowsAffected == 1`; or optimistic concurrency with a `rowversion` column.

### Q22. What's the catch with `ConcurrentDictionary.GetOrAdd(key, factory)`?
The dictionary is thread-safe, but under contention the **factory can run more than once** (only one result is stored). If the factory has side effects or is expensive, wrap the value in `Lazy<T>` (`LazyThreadSafetyMode.ExecutionAndPublication`) so the work starts once.
- *Code:* `IdempotencyStore` uses `ConcurrentDictionary<string, Entry(Lazy<Task<Order>>)>`. *Demo:* `-- races` prints how many times the factory ran.

### Q23. What is a deadlock between locks, and how do you prevent it?
Thread A holds lock 1 and wants 2; thread B holds 2 and wants 1. Prevent with a **global lock ordering**, avoiding nested locks, using timeouts (`Monitor.TryEnter`), and keeping critical sections tiny (never call unknown/async code while holding a lock).

### Q24. What is the memory model concern behind `volatile`/`Volatile.Read`?
Compilers and CPUs reorder and cache reads/writes. `Volatile.Read/Write` (or `lock`/`Interlocked`, which imply barriers) guarantee visibility/ordering across threads. Prefer higher-level constructs; reach for `volatile` only for simple flags.

### Q25. Are singletons in DI automatically thread-safe?
No - the container only controls *lifetime*. A singleton shared by all requests must be **stateless or internally thread-safe**. Also: never inject a **scoped** service into a singleton (captive dependency). This repo keeps state in singletons (`IdempotencyStore`, repository) and makes `OrdersService` scoped.

### Q26. Why shouldn't you `new HttpClient()` per request - and why not a singleton typed client holding on forever?
Per-request clients exhaust sockets (`TIME_WAIT`). A single static client never refreshes DNS. **`IHttpClientFactory`** pools and recycles handlers (default 2 min) and is where we attach retry/circuit-breaker handlers (`AddOrderFlowResilienceHandlers`).

---

## Part 3 - REST API design

### Q27. What makes an API RESTful?
Resources identified by URLs (nouns: `/orders/{id}`), manipulated with HTTP verbs, **stateless** requests, standard status codes, representations (JSON), cacheability, and (ideally) hypermedia. In practice: consistent resource naming, correct verbs/status codes, and predictable errors.

### Q28. Safe vs idempotent HTTP methods?
| Method | Safe | Idempotent | Typical use |
|---|---|---|---|
| GET | yes | yes | read |
| PUT | no | yes | replace whole resource |
| DELETE | no | yes | remove (second call: still "gone") |
| PATCH | no | not guaranteed | partial update |
| POST | no | **no** | create / actions |
- Make POST safe to retry with an **`Idempotency-Key`** header (this repo: `IdempotencyStore`).

### Q29. Which status codes do you return, and when?
`200` OK · `201` Created (+ `Location`) · `204` No Content · `400` malformed/validation · `401` unauthenticated · `403` forbidden · `404` not found · `409` conflict (out of stock, idempotency-key reuse, version conflict) · `412` precondition failed (ETag mismatch) · `422` semantically invalid · `429` rate limited (+ `Retry-After`) · `500` bug · `502/503/504` dependency down/overloaded/timeout.
- Distinguish **4xx (client can fix)** from **5xx (server's problem)**; retries are only appropriate for 5xx/429/timeouts.

### Q30. How do you return errors?
**RFC 7807 `ProblemDetails`** (`application/problem+json`): `type`, `title`, `status`, `detail`, plus extensions (we add `correlationId`). Produced in **one place** (`GlobalExceptionMiddleware`) from domain exceptions, and never leak stack traces or upstream URLs on 5xx.

### Q31. Pagination: offset vs cursor?
Offset (`?page=2&pageSize=20`) is simple but slow on large tables and unstable when rows are inserted. **Cursor/keyset** (`?after=<last id/time>`) is stable and fast but can't jump to page N. Always cap `pageSize` (we enforce 1-100) and return total/next-link metadata.

### Q32. How do you version an API?
URL segment (`/api/v1/...`, used here - simple, cache-friendly), header (`api-version`), or media type. Rules: don't break existing clients, add fields rather than change them, deprecate with notice, run versions side by side.

### Q33. PUT vs PATCH?
`PUT` replaces the entire resource (idempotent). `PATCH` applies a partial change (JSON Patch / Merge Patch). Use PATCH for large resources and partial edits; document its idempotency.

### Q34. How do you handle concurrent updates to the same resource?
**Optimistic concurrency**: return an `ETag` (or version), require `If-Match` on update, respond `412 Precondition Failed` (or `409`) on mismatch. In code: `repository.TryUpdate(newValue, comparison)` in `OrdersService.CancelAsync` loops on conflict; in SQL use a `rowversion` column.

### Q35. How do you make an endpoint cacheable?
`Cache-Control`, `ETag` + `If-None-Match` (`304 Not Modified`), `Last-Modified`; response caching / output caching middleware; CDN for public GETs. Never cache user-specific data in a shared cache without `Vary`/`private`.

### Q36. How do you protect an API?
HTTPS; authentication (JWT/OAuth2/OIDC for users, mTLS or client-credentials for services); authorization (policies/roles/claims); **rate limiting** (built-in limiter: fixed window globally, token bucket on the write endpoint); input validation; CORS only for known origins; no secrets in source (user-secrets/Key Vault). This repo's API key middleware is a *teaching* example of service auth.

### Q37. Minimal APIs vs controllers?
Minimal APIs: less ceremony, great for small services/microservices, endpoint filters, route groups (used here). Controllers: conventions, model binding/validation attributes, filters, better for large APIs with many cross-cutting rules. Both share the same pipeline.

### Q38. Why DTOs instead of exposing entities?
Decouples the contract from the storage model, prevents over-posting/mass assignment, hides internal fields, allows independent evolution.

---

## Part 4 - Middleware and the ASP.NET Core pipeline

### Q39. What is middleware?
A component in the request pipeline that receives the `HttpContext`, may do work **before** calling `next`, and **after** `next` returns on the way back. It can **short-circuit** by not calling `next` (e.g., return 401).

### Q40. `Use`, `Run`, `Map` - what's the difference?
`Use` adds middleware that can call `next`; `Run` is terminal (no `next`); `Map`/`MapWhen` branch the pipeline by path/condition.

### Q41. Why does order matter? What's the order in this repo?
Requests flow top→bottom, responses bottom→top. Ours: **correlation id → timing → exception handler → (swagger) → API key/rate limiter → endpoints**.
- Correlation first so everything (including error logs) carries the id. Timing outside the exception handler so it measures errors too. Exception handler early so it catches exceptions from everything below. Auth/rate-limit before real work so rejected calls are cheap.
- Typical framework order: exception handling → HTTPS redirection → static files → routing → CORS → authentication → authorization → endpoints.

### Q42. How do you write custom middleware?
A class with a constructor taking `RequestDelegate next` (and singletons) and an `InvokeAsync(HttpContext)` method; register with `app.UseMiddleware<T>()`. **Scoped** services must be injected into `InvokeAsync` parameters (middleware is created once). Alternative: implement `IMiddleware` (resolved per request from DI).
- *Code:* `OrderFlow.Shared/Middleware/*`.

### Q43. Why does `RequestTimingMiddleware` set its header in `Response.OnStarting`?
Once the response body starts being sent, headers are frozen. `OnStarting` runs just before that, the last safe moment to add a header with the final elapsed time.

### Q44. Middleware vs filters vs endpoint filters vs `DelegatingHandler`?
- **Middleware**: whole pipeline, HTTP-level, before routing knows the endpoint.
- **MVC filters / endpoint filters**: run around a specific action/endpoint, know the model-bound arguments/result.
- **`DelegatingHandler`**: the same chain-of-responsibility idea, but for **outgoing** `HttpClient` calls (retry, circuit breaker, correlation id forwarding).

### Q45. How do you handle exceptions globally?
Either `UseExceptionHandler`/`IExceptionHandler` (built-in) or custom middleware (this repo) that maps domain exceptions to status codes + `ProblemDetails`, logs at the right level (4xx = warning, 5xx = error), special-cases client aborts (`499`, no body) and the "response already started" case.

### Q46. What happens if middleware throws after the response has started?
You can't change the status or headers any more. Log it and let the server abort the connection (our middleware re-throws when `Response.HasStarted`).

---

## Part 5 - Microservices and resilience

### Q47. When should you *not* use microservices?
Small team, unclear domain boundaries, no DevOps maturity. Costs: distributed failure modes, eventual consistency, observability, deployment complexity, versioning. A **modular monolith** is often the better start; extract services when a boundary and a scaling/ownership need are proven.

### Q48. How do you decide service boundaries?
By **business capability / bounded context** (DDD), not by technical layer: Orders and Inventory change for different reasons, have different owners and scale differently. Each service **owns its data** (no shared database).

### Q49. Synchronous (HTTP/gRPC) vs asynchronous (messaging) communication?
Sync: simple request/response, immediate answer, but **temporal coupling** (callee must be up) and failure cascades. Async messaging/events: decoupled, absorbs spikes, enables retries, but eventual consistency and harder debugging. Orders → Inventory reservation is sync because the customer needs an immediate answer; "order confirmed" notifications are async (the channel).

### Q50. How do you keep data consistent without distributed transactions?
**Saga**: a sequence of local transactions with **compensating actions**. *Orchestration*: a coordinator drives steps (`OrdersService`). *Choreography*: services react to each other's events. Plus **idempotency** everywhere (messages are delivered at-least-once) and the **outbox pattern** to publish events atomically with the DB write.
- *Code:* reserve all lines in parallel → if any fails → `SafeReleaseAsync` → rethrow. *Test:* `Out_of_stock_line_rejects_order_and_releases_reservations`.

### Q51. Explain the transactional outbox.
Write the business change **and** an "outbox" row in the **same local DB transaction**; a separate publisher reads the outbox and sends to the broker, marking rows sent. This avoids the dual-write problem (DB commit succeeded but publish failed, or vice-versa). Consumers must still be idempotent.

### Q52. Which resilience patterns do you use and why?
- **Timeout** - never wait forever; per-attempt *and* overall.
- **Retry with exponential backoff + jitter** - fixes transient faults; jitter prevents synchronized retry storms; retry **only idempotent** operations and only transient errors (5xx/timeouts/429, not 4xx).
- **Circuit breaker** - after N consecutive failures, fail fast for a cool-down; one probe in half-open. Protects the dependency *and* your own threads.
- **Bulkhead** - isolate resources (separate pools/semaphores) so one slow dependency can't consume everything.
- **Fallback / graceful degradation** - cached data, default response.
- *Code:* `OrderFlow.Shared/Resilience`. *Tests:* `CircuitBreakerTests` use a fake `TimeProvider`, no sleeping.

### Q53. Why is the handler order retry → breaker → timeout?
Retry outside the breaker means each retry is *counted by* the breaker and can be *rejected by* it (we don't keep retrying an open circuit - `BrokenCircuitException` isn't retried). The per-attempt timeout sits innermost so each attempt gets its own budget.

### Q54. What is idempotency and how did you implement it?
Applying an operation many times has the same effect as once. Needed because retries and duplicate deliveries are unavoidable. Here: (1) **client-supplied `Idempotency-Key`** on `POST /orders` → same key returns the same order; same key with a different body → `409`; failures aren't cached. (2) **Natural idempotency** in Inventory: reservations keyed by `(orderId, sku)`; release is a no-op when repeated.
- Production additions: persistent store, TTL, store the original response.

### Q55. How do you trace one request across services?
A **correlation id** (we use `X-Correlation-ID`): generated at the edge, put into the logging scope, forwarded by `CorrelationIdHandler` on every outgoing call, and returned in error bodies. For real systems: **OpenTelemetry** distributed traces (W3C `traceparent`) + structured logs + metrics, collected in Jaeger/Application Insights.

### Q56. Liveness vs readiness health checks?
**Liveness**: "is the process alive?" (restart if not). **Readiness**: "can it take traffic?" (dependencies reachable; remove from the load balancer if not). Keep liveness cheap; don't make it depend on downstream services or one outage restarts everything.

### Q57. What does an API gateway do?
Single entry point: routing, TLS termination, authentication, rate limiting, request aggregation, caching. Variants: **BFF** (one backend per frontend type). .NET options: YARP, Ocelot, Azure API Management.

### Q58. How do you test microservices?
Many fast unit tests (domain logic, concurrency, state machines), fewer **integration** tests (`WebApplicationFactory`, Testcontainers for DB/broker), **contract tests** (consumer-driven, e.g. Pact) so services can deploy independently, and a few end-to-end tests. Fake the clock (`TimeProvider`) instead of sleeping.

### Q59. How do you version and deploy without breaking consumers?
Backward-compatible changes first (add, don't remove/rename), expand/contract for schema changes, feature flags, blue/green or canary, health-gated rollout, and contract tests in CI.

### Q60. How do you secure service-to-service calls?
Zero-trust mindset: mTLS or OAuth2 client-credentials (short-lived JWTs), least-privilege scopes, secrets in a vault, network policies. Shared API keys (as in this demo) are the simplest form and should be rotated.

---

## Part 6 - "How did you solve it?" stories

Each story is: **Problem → Root cause → Fix → Proof → Follow-up questions**. Replace the *Result* placeholders with real numbers from your own work, or say you reproduced it in OrderFlow.

### Story 1 - Overselling under load (race condition)
- **Situation:** During a flash sale, orders were accepted for items that were out of stock; stock went negative.
- **Root cause:** Check-then-act race: *read stock → check ≥ qty → write stock − qty* are separate steps; two requests read the same value.
- **Action:** Made the reservation **atomic**: CAS loop in `StockItem.TryReserve` (in a DB: `UPDATE … SET Qty = Qty - @n WHERE Qty >= @n` + check rows affected, or `rowversion` optimistic concurrency). Wrote a test hammering it with 2 000 parallel calls.
- **Result:** *[your numbers: oversells per hour before → 0 after; latency impact]*.
- **Follow-ups:** Why not just `lock`? (Works in one process, fails across instances; atomic DB statement scales out.) How does it behave with many instances? (Push the atomicity into the shared store.)

### Story 2 - Duplicate orders from client retries
- **Situation:** Mobile clients on poor networks timed out and retried; customers got charged/ordered twice.
- **Root cause:** `POST` isn't idempotent; the first request had actually succeeded.
- **Action:** Introduced the **`Idempotency-Key`** header. `IdempotencyStore` runs the work once per key (`Lazy<Task<T>>`), returns the same result for repeats, **rejects key reuse with a different payload (409)** via a request fingerprint, and **doesn't cache failures**. Made Inventory reservations naturally idempotent too.
- **Result:** *[duplicate-order rate → 0]*. Test: `Concurrent_requests_with_same_idempotency_key_create_exactly_one_order` (20 concurrent calls → 1 order, 1 reservation).
- **Follow-ups:** How long do you keep keys? (TTL, e.g. 24 h, in Redis/DB.) What if the process crashes mid-request? (Persist key + status; a recovery job or re-execution with the natural idempotency.)

### Story 3 - A slow dependency took the whole API down (cascading failure)
- **Situation:** Inventory slowed down; Orders threads piled up waiting; Orders itself became unresponsive.
- **Root cause:** No timeouts, unlimited retries, no isolation: slow calls held resources, retries multiplied load on the already-struggling service.
- **Action:** Per-attempt **timeout (2 s)**, **bounded retries with exponential backoff and jitter**, a **circuit breaker** (open after 5 consecutive failures, 15 s cool-down, single half-open probe), map open circuit → fast `503`. Reproduced with the **chaos endpoint**.
- **Result:** *[p99 during incident before/after; error rate; recovery time]*. Show `GET /api/v1/diagnostics` flipping Closed → Open → HalfOpen → Closed.
- **Follow-ups:** Why jitter? Why not retry POST blindly? What do you return to the user while open? (Fallback/clear 503 with `Retry-After`.)

### Story 4 - Slow API but CPU was low (thread-pool starvation)
- **Situation:** Latency spiked under moderate load; CPU stayed ~10%.
- **Root cause:** Handlers called `.Result` on async code, blocking pool threads; the pool couldn't inject threads fast enough.
- **Action:** Diagnosed with `dotnet-counters` (queue length climbing, thread count rising) and a stack dump (many threads in `Task.Wait`). Fixed by making the call chain **async end-to-end**, removing `Task.Run` wrappers around I/O, adding `CancellationToken`s and throttling with `SemaphoreSlim`.
- **Result:** *[p95 before/after; threads]*. Reproduce with `ConcurrencyLab -- starvation`.
- **Follow-ups:** Why not just `SetMinThreads`? (Hides the bug, costs memory.) Why can't ASP.NET Core deadlock like WinForms? (No sync context - but it *can* starve.)

### Story 5 - An endpoint made 5 sequential calls (latency)
- **Situation:** `POST /orders` took ~N × per-call latency because stock was reserved line by line.
- **Action:** Started all reservations together and awaited `Task.WhenAll` → latency ≈ slowest call. Added **compensation** so a partial failure releases what succeeded, and kept the total bounded (max 20 lines per order, per-attempt timeouts).
- **Result:** *[e.g. 5 × 200 ms = 1 s → ~200 ms]* (see `ConcurrencyLab -- async`).
- **Follow-ups:** What if an order has 500 lines? (Throttle with `Parallel.ForEachAsync`/batch endpoint.) Why wait for *all* tasks on failure? (So compensation doesn't race with in-flight reservations.)

### Story 6 - Half-completed order across two services (distributed consistency)
- **Situation:** Stock was reserved for line A, line B failed → stock "leaked".
- **Action:** **Saga with compensation**: any failure triggers `ReleaseAsync(orderId)` using `CancellationToken.None` so cleanup runs even if the caller disconnected; release is idempotent; if release itself fails we log for reconciliation and (next step) use an **outbox**.
- **Result:** *[leaked reservations → 0]*; test `Out_of_stock_line_rejects_order_and_releases_reservations`.
- **Follow-ups:** Orchestration vs choreography? Why not 2PC? (Locks, availability, not supported by most HTTP/NoSQL services.)

### Story 7 - "Which service broke this request?" (observability)
- **Action:** `CorrelationIdMiddleware` + logging scope + `CorrelationIdHandler` forwarding the header + `correlationId` in every `ProblemDetails`. Support can search one id across both services' logs.
- **Result:** *[mean time to diagnose: hours → minutes]*. **Next step:** OpenTelemetry traces.

### Story 8 - Memory growing in a worker (unbounded queue)
- **Root cause:** Producers outpaced the consumer; an unbounded queue grew until OOM.
- **Action:** Replaced with a **bounded `Channel<T>`** (`FullMode = Wait`) = backpressure, plus consumers with bounded parallelism (`Parallel.ForEachAsync`, 4). Discussed shutdown draining.
- **Follow-ups:** What should happen when the queue is full - wait, drop, or fail fast? (Depends on whether data loss is acceptable → durable broker.)

---

## Part 7 - Rapid fire

| Question | One-line answer |
|---|---|
| Is a `Task` a thread? | No - a promise; it may use no thread at all. |
| `Task.Delay` vs `Thread.Sleep`? | Delay = timer, frees the thread; Sleep blocks it. |
| `Task.Run` in a controller for DB calls? | Don't - use the async DB API. |
| `lock` with `await` inside? | Compile error; use `SemaphoreSlim`. |
| `Task.WhenAll` exception? | `await` throws the first; others in `.Exception.InnerExceptions`. |
| `async void` OK when? | Event handlers only. |
| `ConfigureAwait(false)` in ASP.NET Core? | Little effect, still good in libraries. |
| `ConcurrentDictionary` `GetOrAdd` factory runs once? | Not guaranteed - use `Lazy<T>`. |
| Singleton depends on scoped? | Captive dependency - not allowed/bug. |
| Safe + idempotent verbs? | GET (safe & idempotent); PUT/DELETE (idempotent). |
| 401 vs 403? | 401 not authenticated; 403 authenticated but not allowed. |
| 400 vs 422 vs 409? | Malformed / semantically invalid / conflicts with current state. |
| Retry which errors? | Transient: timeouts, 5xx, 429 - not 4xx. |
| Why jitter? | De-synchronise clients so retries don't stampede. |
| Circuit breaker states? | Closed → Open → Half-open (single probe) → Closed/Open. |
| Liveness vs readiness? | Alive vs able to serve traffic. |
| Saga vs 2PC? | Local transactions + compensation vs global locking commit. |
| Outbox solves? | Atomic "save + publish" (dual-write problem). |
| Why bounded channel? | Backpressure - memory stays bounded. |
| Middleware order rule? | Exception handling outermost; auth/rate-limit before heavy work. |

---

## Part 8 - Coding drills

Practise these on a whiteboard/IDE; each maps to code here.

1. **Thread-safe counter / stats** with `Interlocked` (see `OrderStats`).
2. **Throttled downloader**: process 1 000 URLs, max 8 concurrent, with cancellation and a per-item timeout (`SemaphoreSlim` or `Parallel.ForEachAsync`).
3. **Async cache with single-flight**: `GetOrAddAsync(key, factory)` where the factory runs once per key even under concurrency (`Lazy<Task<T>>`) - and doesn't cache failures (`IdempotencyStore`).
4. **Retry helper**: `RetryAsync(Func<CancellationToken, Task<T>>, maxRetries)` with exponential backoff + jitter, observing cancellation (`RetryHandler`).
5. **Circuit breaker** state machine with an injectable clock (`CircuitBreaker` + tests).
6. **Producer/consumer** with a bounded `Channel<T>`, 1 producer / N consumers, graceful completion (`channels` demo).
7. **Rate limiter**: token bucket from scratch (refill timer or lazy refill on each call).
8. **Custom middleware**: log request/response size, or add `Retry-After` on 429; write a test with `DefaultHttpContext`.
9. **Reproduce and fix a deadlock**: sync-over-async with a single-thread `SynchronizationContext` (`deadlock` demo).
10. **Parallel aggregation**: call 3 services concurrently, return partial results if one times out (`WhenAll` + per-call timeout + fallback).
