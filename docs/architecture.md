# Source architecture

SubTru uses a layered architecture with feature-oriented folders inside each layer.
The project boundaries describe what kind of code is present, and the folders within
them describe the product capability or infrastructure integration involved.

## Categories

1. **Core business logic** lives in `SubTru.Domain` and defines
   provider-independent subscription concepts and access decisions.
2. **Product features** live in `SubTru.Api/Features` and contain the HTTP
   endpoints and contracts for capabilities such as projects, API keys,
   subscriptions, and webhooks.
3. **Infrastructure integrations** live in `SubTru.Infrastructure` and handle
   communication with PostgreSQL and external providers, along with supporting
   security concerns such as cryptography.
4. **Application plumbing** lives in `SubTru.Api/Platform` and
   `SubTru.Api/Bootstrap` and handles authentication, HTTP behavior, health,
   observability, startup, and application wiring.

The dependency direction is:

```text
SubTru.Api -> SubTru.Domain
SubTru.Api -> SubTru.Infrastructure -> SubTru.Domain
```

`SubTru.Domain` must remain free of ASP.NET Core, PostgreSQL, and provider SDK
dependencies.

## Placing new code

- Put business logic that applies across all providers in `SubTru.Domain`.
- Put endpoints and request/response models in the matching
  `SubTru.Api/Features` folder.
- Put provider SDK behavior in `SubTru.Infrastructure/Providers/<Provider>`.
- Put PostgreSQL repositories and database-access code in the appropriate feature
  folder under `SubTru.Infrastructure/Persistence/Postgres`.
- Put shared API infrastructure in the appropriate `SubTru.Api/Platform`
  subfolder, such as `Authentication`, `Health`, `Http`, or `Observability`.
- Add dependency injection, startup validation, pipeline, or endpoint wiring to
  `SubTru.Api/Bootstrap`.

Avoid generic `Common`, `Helpers`, or `Services` folders. If code is shared,
name the shared capability or mechanism explicitly.

Tests live in `tests/SubTru.Tests` and mirror the production categories under
`Domain`, `Features`, `Infrastructure`, `Platform`, and `Support`.
