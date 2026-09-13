# Repository enrollment

The LFS gateway owns UUIDs, storage bindings, credentials and grants. The website
and operator are clients. Source URLs and provider IDs never confer ownership.
Trusted issuers and provisioner authority are separate: a signed token must have
the LFS audience, and its exact issuer/subject must be an enabled provisioner.

`PUT /api/v1/enrollments/{caller-owned-key}` accepts:

```json
{"displayName":"Repository","managerKey":"project-123","grants":[{"issuer":"https://identity.example","subject":"worker","permission":"write"}],"repositoryCredential":true}
```

The response contains `id`, `url` and an optional sensitive `credential`.
Reconciliation preserves the UUID and credential. New storage is `r/<uuid>/`;
callers cannot choose an existing UUID or storage path. Enrollment keys are scoped
to the caller, independently of Git provider. `GET` returns enrollment metadata;
only 404 means absent. Responses carrying credentials use `Cache-Control: no-store`.

Owners or principals with `manage` permission can reconcile grants with
`PUT /api/v1/repositories/{uuid}/grants/{managerKey}`. Only that caller's grants
under that manager key change; other controllers' grants remain intact.
`DELETE /api/v1/repositories/{uuid}/credentials` revokes committed credentials.
Normal reconciliation cannot reactivate a revoked credential. Workload grants
are reconciled separately. Already issued URLs expire within five minutes,
capped by the source identity expiry.

Legacy `/organisation/repository` routes retain their existing backend. UUID
routes require an active database grant or a repository-bound credential and never
fall back to broad legacy storage credentials. Gateway-signed storage tokens carry
the database prefix and a read/write policy for SeaweedFS STS. Private signing and
versioned credential HMAC keys are mounted secrets. Retain old HMAC versions during
rotation until their committed credentials are deliberately revoked.

## Migrations and imports

Use a separate GitOps migration workload with the database owner:

```
dotnet Estranged.Lfs.Hosting.Web.dll --migrate --import-repositories /config/repositories.json --grant-runtime-role
```

Web replicas use `lfs_app` with DML privileges and no schema ownership. `/readyz`
checks database connectivity and pending EF migrations. Reviewed legacy imports
name UUID, literal storage directory, owner identity and enrollment key. They do
not move objects or create aliases. Conflicting prefixes, changed directories and
reassigned enrollment keys fail atomically. Ordinary provisioning uses the API.

## Tests

`dotnet test tests/Estranged.Lfs.Tests` runs legacy and mocked S3/STS tests.
`scripts/test-registry.sh` starts disposable PostgreSQL, tests migrations,
concurrency, imports and authorization, then removes the container. Alternatively
set `LFS_TEST_POSTGRES` to a dedicated test server, never an application database.
JWT tests use generated test keys and mocked discovery. Identity validation and
storage-token issuance are strict mocks in database authorization tests. CI runs
both suites.
