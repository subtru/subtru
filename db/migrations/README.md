# Database migrations

Run these files to set up your database for self-hosting SubTru.

## Order

1. `001_projects.sql`: shared PostgreSQL helpers and top-level projects.
2. `002_subscriptions.sql`: normalized subscription state and access data.
3. `003_api_keys.sql`: project and environment-scoped API keys.
4. `004_webhook_keys.sql`: provider webhook routing keys and encrypted secrets.
5. `005_webhook_events.sql`: webhook diagnostics and idempotency records.
6. `006_provider_integrations.sql`: Apple and Google provider configuration.
7. `007_google_purchase_remediations.sql`: audit records for manual Google remediation.
8. `008_google_subscription_snapshots.sql`: encrypted Google API diagnostic snapshots.
9. `009_runtime_database_security.sql`: optional least-privilege runtime roles.

From the repository root, apply the required migrations in filename order:

```bash
for file in db/migrations/00[1-8]_*.sql; do
  psql "$DATABASE_URL" -v ON_ERROR_STOP=1 -f "$file"
done
```

Migration `009` is recommended for production but optional when your database user is already restricted by your hosting provider. Apply it separately if you want SubTru's least-privilege runtime roles:

```bash
psql "$DATABASE_URL" -v ON_ERROR_STOP=1 -f db/migrations/009_runtime_database_security.sql
```

## Runtime role

Migration `009` creates:

- `subtru_api_runtime`, a no-login privilege role with only the permissions the API needs.
- `subtru_api_app`, a login role that inherits `subtru_api_runtime`.

Set the login role password without exposing it in shell history or SQL logs:

```bash
psql "$DATABASE_URL"
```

Then run this inside `psql` and enter a generated password when prompted:

```psql
\password subtru_api_app
```

Then configure the `ConnectionStrings__Postgres` secret to use `subtru_api_app`. Run future migrations with the same owner role when possible and explicitly grant access to any new runtime tables or functions.

Migration `009` configures access to SubTru’s schema and database objects. It does not change database-level `CONNECT` permissions, which remain the database operator’s responsibility.

Database network access should also be private or restricted to the application. PostgreSQL grants do not replace firewall, VPC, or provider allowlist controls.
