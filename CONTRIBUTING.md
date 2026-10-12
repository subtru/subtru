# Contributing to SubTru

Bug reports, documentation fixes, and code contributions are welcome.

## Report an issue

Do not open a public issue for a suspected security vulnerability. Follow the
private reporting process in [SECURITY.md](SECURITY.md).

Open an issue in this repository with the behavior you expected, what happened, and steps to reproduce it. For provider or webhook problems, include the provider, `test` or `production` environment, and relevant SubTru error codes or sanitized logs. Do not include API keys, webhook URLs, signing secrets, purchase tokens, service-account credentials, or raw webhook payloads.

For a larger feature or design change, open an issue first so the approach can be discussed before you spend time implementing it.

## Make a change

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). Read the [source architecture](docs/architecture.md) before changing project boundaries or adding new folders.

From the repository root, build and run the tests:

```bash
dotnet build src/SubTru.sln -c Release
dotnet test src/SubTru.sln -c Release
```

PostgreSQL integration tests are skipped unless `SUBTRU_TEST_DB_CONNECTION` points to a test database. To run them, use a disposable database and apply the migrations in `db/migrations` first. See the [migration guide](db/migrations/README.md) for the order and the optional runtime role.

Add focused tests for behavior changes. If you change an endpoint, provider setup, or deployment behavior, update the relevant documentation. For database changes, add the next migration and update the migration guide, including runtime-role grants where needed.

## Open a pull request

Describe the problem, the change, and how you tested it. Link the related issue if there is one. Keep unrelated changes out of the pull request and remove any credentials or private customer data before submitting it.

Contributions submitted for inclusion are handled under Section 5 of the [Apache License, Version 2.0](LICENSE).
