# Study plan - work through the repo

The goal of every block: **run it → break it → explain it out loud in 60 seconds.** Explaining aloud is what interviews test.

## Today (≈ 6 h)

| Block | Time | Do this |
|---|---|---|
| 1. Setup | 20 min | Install .NET 8 SDK. `dotnet build && dotnet test`. Fix anything that doesn't compile (paste errors to Claude if stuck). |
| 2. ConcurrencyLab | 90 min | Run each demo (`dotnet run --project src/ConcurrencyLab -- <name>`): `threads`, `async`, `races`, `throttle`, `cancel`, `channels`, `deadlock`, `exceptions`, `starvation`. For each: read the code, predict the output, run, explain it aloud. Then read **Part 1 + 2** of `INTERVIEW_QA.md`. |
| 3. Middleware | 45 min | Read `OrderFlow.Shared/Middleware/*`. Reorder the pipeline in `Orders.Api/Program.cs` and observe what breaks (e.g., put the exception handler last). Add a tiny middleware of your own (e.g., `X-Request-Size`). Read **Part 4**. |
| 4. Run the services | 60 min | Start both APIs; run every request in `requests.http`. Turn on the chaos endpoint and watch retries → circuit open → recovery. Read **Part 5**. |
| 5. Tests | 30 min | Run `dotnet test`. Add one test of your own (e.g., idempotency key reuse after a failure, or a new circuit-breaker case). |
| 6. Stories | 45 min | Read **Part 6**. For two stories, write your *own* version with a real example from your career (use real numbers). Practise telling each in 2 minutes. |
| 7. GitHub | 20 min | Publish the repo (commands below), pin it on your profile, write a good description and topics. |

## Publish to GitHub

```bash
cd OrderFlow
git init -b main
git add .
git commit -m "Initial commit: OrderFlow - concurrency lab, orders & inventory microservices"
# create an empty repo named OrderFlow on github.com first, then:
git remote add origin https://github.com/<your-username>/OrderFlow.git
git push -u origin main
```

Make the profile work for you:
- Repo **description**: *".NET 8 microservices demo: async/await & concurrency lab, custom middleware, retry/circuit-breaker, idempotent REST APIs"*
- **Topics**: `dotnet`, `csharp`, `microservices`, `aspnet-core`, `rest-api`, `async-await`, `concurrency`, `resilience`, `circuit-breaker`, `xunit`
- Pin the repo; add a short **profile README** linking to it. Commit in small, meaningful steps from now on (green CI badge + a visible history looks far better than one giant commit).
- After pushing, check the **Actions** tab - the CI workflow builds and runs the tests on every push.

## Next days (each adds a feature = a new commit + new interview topics)

| Day | Feature | Interview topics it unlocks |
|---|---|---|
| 2 | Replace in-memory stores with **EF Core + SQL Server** (Docker), `rowversion` optimistic concurrency, migrations | transactions, isolation levels, N+1, concurrency tokens |
| 3 | **YARP gateway** in front of both services; JWT auth on the public API | gateway/BFF, authN vs authZ, token propagation |
| 4 | **Outbox pattern** + a real broker (RabbitMQ or Azure Service Bus); idempotent consumer | at-least-once delivery, ordering, DLQ, eventual consistency |
| 5 | **OpenTelemetry** traces/metrics → Jaeger or Application Insights; structured logging with Serilog | distributed tracing, SLOs, alerting |
| 6 | **Integration tests** with `WebApplicationFactory` + Testcontainers; load test with k6/NBomber | testing strategy, perf bottlenecks, capacity |
| 7 | Deploy to **Azure Container Apps / App Service**, add GitHub Actions CD, Key Vault for secrets | CI/CD, config & secrets, health probes, scaling |

Days 4-7 line up well with Azure certification topics (Service Bus, App Insights, Key Vault, Container Apps) if you're preparing for AZ-204.
